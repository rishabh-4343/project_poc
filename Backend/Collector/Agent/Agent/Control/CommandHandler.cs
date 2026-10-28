using System.Text.Json;
using System.Text.Json.Serialization;
using Auditor.Agent.Pipeline;

namespace Auditor.Agent.Control;

// Applies StartScope/StopScope commands from the backend. The backend resends unanswered commands after a
// reconnect, so a repeated command id gets the same answer again instead of being applied twice.
public sealed class CommandHandler(ScopeManager scopes, TimeProvider clock, ILogger<CommandHandler> logger)
{
    private const int RememberedAnswers = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly Dictionary<Guid, CommandResultMessage> _answered = new();
    private readonly Queue<Guid> _order = new();

    public CommandResultMessage Handle(AgentCommand command)
    {
        lock (_gate)
        {
            if (_answered.TryGetValue(command.CommandId, out var previous))
            {
                logger.LogInformation("Duplicate command {CommandId} ({Type}); repeating the earlier answer", command.CommandId, command.Type);
                return previous;
            }

            var result = Apply(command);
            _answered[command.CommandId] = result;
            _order.Enqueue(command.CommandId);
            while (_order.Count > RememberedAnswers)
            {
                _answered.Remove(_order.Dequeue());
            }

            return result;
        }
    }

    private CommandResultMessage Apply(AgentCommand command)
    {
        if (command.ExpiresAt < clock.GetUtcNow())
        {
            return Fail(command, "Command expired before it was processed.");
        }

        try
        {
            switch (command.Type)
            {
                case "StartScope":
                    var scope = command.Payload.Deserialize<StartScopeCommand>(Json)
                        ?? throw new JsonException("Empty payload.");
                    if (!scopes.Start(scope))
                    {
                        return Fail(command, "Scope needs at least one folder and one operation.");
                    }

                    logger.LogInformation("StartScope {SessionId}: {Folders} folder(s)", scope.SessionId, scope.Folders.Count);
                    return Ok(command);

                case "StopScope":
                    var stop = command.Payload.Deserialize<StopScopeCommand>(Json)
                        ?? throw new JsonException("Empty payload.");
                    // Stopping a scope that is not held is still a success: the end state is what the backend wants.
                    scopes.Stop(stop.SessionId);
                    logger.LogInformation("StopScope {SessionId}", stop.SessionId);
                    return Ok(command);

                default:
                    return Fail(command, $"Unknown command type {command.Type}.");
            }
        }
        catch (JsonException ex)
        {
            return Fail(command, $"Bad {command.Type} payload: {ex.Message}");
        }
    }

    private static CommandResultMessage Ok(AgentCommand c) => new(c.CommandId, true, null, null);

    private CommandResultMessage Fail(AgentCommand c, string error)
    {
        logger.LogWarning("Command {CommandId} ({Type}) failed: {Error}", c.CommandId, c.Type, error);
        return new CommandResultMessage(c.CommandId, false, error, null);
    }
}
