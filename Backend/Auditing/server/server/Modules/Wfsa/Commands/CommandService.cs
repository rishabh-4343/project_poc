using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using server.Common.Auth;
using server.Common.Hubs;
using server.Modules.Wfsa.Data;
using server.Modules.Wfsa.Sessions;

namespace server.Modules.Wfsa.Commands;

public sealed record ScopeFolder(string LocalPath, bool Recursive, Guid? ShareId);
public sealed record StartScopePayload(
    Guid SessionId, Guid ServerId, List<Operation> Operations, bool IncludeReads, List<ScopeFolder> Folders, DateTimeOffset? MaxEndAt);
public sealed record StopScopePayload(Guid SessionId);

// What the agent receives; the client method name is the command type (StartScope, StopScope).
public sealed record AgentCommand(Guid CommandId, string Type, DateTimeOffset ExpiresAt, JsonElement Payload);
public sealed record CommandResultMessage(Guid CommandId, bool Success, string? Error, JsonElement? Result);

public static class CommandTypes
{
    public const string StartScope = "StartScope";
    public const string StopScope = "StopScope";
}

public static class CommandStatuses
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Expired = "Expired";
}

public sealed class CommandService(
    WfsaDbContext db,
    IHubContext<AgentHub> hub,
    AgentConnectionTracker tracker,
    SessionNotifier notifier,
    IConfiguration configuration,
    ILogger<CommandService> logger)
{
    public const string ModuleSlug = "wfsa";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private TimeSpan Ttl => TimeSpan.FromSeconds(configuration.GetValue("Wfsa:CommandTtlSeconds", 120));

    // The session must already be tracked by the context so both rows are saved in one transaction.
    public async Task IssueAsync(SessionRecord session, string type, CancellationToken ct)
    {
        object payload = type == CommandTypes.StartScope
            ? new StartScopePayload(
                session.Id, session.ServerId, session.Operations, session.IncludeReads,
                session.Folders.Select(f => new ScopeFolder(f.LocalPath, f.Recursive, f.ShareId)).ToList(), session.MaxEndAt)
            : new StopScopePayload(session.Id);

        var now = DateTimeOffset.UtcNow;
        var command = new CommandRecord
        {
            Id = Guid.NewGuid(),
            TenantId = session.TenantId,
            AgentId = session.AgentId,
            Module = ModuleSlug,
            Type = type,
            Payload = JsonSerializer.Serialize(payload, Json),
            RefId = session.Id,
            Status = CommandStatuses.Pending,
            IssuedAt = now,
            ExpiresAt = now + Ttl,
        };
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);

        if (tracker.IsOnline(session.AgentId))
        {
            await DeliverAsync(command, session, ct);
        }
    }

    // Called when an agent connects: anything it missed while offline is sent again.
    public async Task ResendPendingAsync(AgentIdentity agent, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var commands = await db.Commands
            .Where(c => c.Module == ModuleSlug && c.AgentId == agent.AgentId && c.TenantId == agent.TenantId
                && (c.Status == CommandStatuses.Pending || c.Status == CommandStatuses.Sent) && c.ExpiresAt > now)
            .OrderBy(c => c.IssuedAt)
            .ToListAsync(ct);

        foreach (var command in commands)
        {
            var session = command.RefId is { } id ? await db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct) : null;
            var before = session?.State;
            await DeliverAsync(command, session, ct);

            if (session is not null && session.State != before)
            {
                await notifier.PublishAsync(session, ct);
            }
        }
    }

    // An agent that restarted has lost its scopes, so every running session gets its StartScope again.
    public async Task ResendActiveScopesAsync(AgentIdentity agent, CancellationToken ct)
    {
        var unanswered = db.Commands.Where(c =>
            c.Module == ModuleSlug && c.Type == CommandTypes.StartScope
            && (c.Status == CommandStatuses.Pending || c.Status == CommandStatuses.Sent));

        var sessions = await db.Sessions.Include(s => s.Folders)
            .Where(s => s.AgentId == agent.AgentId && s.TenantId == agent.TenantId
                && (s.State == SessionState.Active || s.State == SessionState.Degraded)
                && !unanswered.Any(c => c.RefId == s.Id))
            .ToListAsync(ct);

        foreach (var session in sessions)
        {
            await IssueAsync(session, CommandTypes.StartScope, ct);
        }
    }

    public async Task CompleteAsync(AgentIdentity agent, CommandResultMessage result, CancellationToken ct)
    {
        var command = await db.Commands.FirstOrDefaultAsync(
            c => c.Id == result.CommandId && c.AgentId == agent.AgentId && c.TenantId == agent.TenantId && c.Module == ModuleSlug, ct)
            ?? throw new HubException("Unknown command.");

        // A repeated or late result for a finished command changes nothing.
        if (command.Status is not (CommandStatuses.Pending or CommandStatuses.Sent))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        command.Status = result.Success ? CommandStatuses.Succeeded : CommandStatuses.Failed;
        command.CompletedAt = now;
        command.Error = result.Success ? null : result.Error ?? "Agent reported failure.";
        command.Result = result.Result?.GetRawText();

        var session = command.RefId is { } id ? await db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct) : null;
        var before = session?.State;
        if (session is not null)
        {
            Apply(command, session, now);
        }

        await db.SaveChangesAsync(ct);

        if (session is not null && session.State != before)
        {
            await notifier.PublishAsync(session, ct);
        }
    }

    public async Task CancelPendingAsync(Guid sessionId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = await db.Commands
            .Where(c => c.RefId == sessionId && c.Status == CommandStatuses.Pending)
            .ToListAsync(ct);

        foreach (var command in pending)
        {
            command.Status = CommandStatuses.Failed;
            command.Error = "Cancelled before delivery.";
            command.CompletedAt = now;
        }
    }

    // Sessions past their MaxEndAt are stopped like a manual stop; the agent's StopScope result completes them.
    public async Task<int> StopOverdueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var overdue = await db.Sessions.Include(s => s.Folders)
            .Where(s => s.MaxEndAt != null && s.MaxEndAt <= now && (s.State == SessionState.Active || s.State == SessionState.Degraded))
            .ToListAsync(ct);

        foreach (var session in overdue)
        {
            session.State = SessionState.Stopping;
            await IssueAsync(session, CommandTypes.StopScope, ct);
            await notifier.PublishAsync(session, ct);
        }

        return overdue.Count;
    }

    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await db.Commands
            .Where(c => c.Module == ModuleSlug && (c.Status == CommandStatuses.Pending || c.Status == CommandStatuses.Sent) && c.ExpiresAt < now)
            .ToListAsync(ct);

        var changed = new List<SessionRecord>();

        foreach (var command in expired)
        {
            command.Status = CommandStatuses.Expired;
            command.Error = "Expired before the agent responded.";
            command.CompletedAt = now;

            var session = command.RefId is { } id ? await db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct) : null;
            if (session is null)
            {
                continue;
            }

            var before = session.State;
            if (command.Type == CommandTypes.StartScope && session.State is SessionState.Requested or SessionState.Starting)
            {
                Fail(session, "Start command expired.", now);
            }
            else if (command.Type == CommandTypes.StartScope && session.State == SessionState.Active)
            {
                session.State = SessionState.Degraded;
                session.FailureReason = "Scope resync expired.";
            }
            else if (command.Type == CommandTypes.StopScope && session.State == SessionState.Stopping)
            {
                Fail(session, "Stop command expired.", now);
            }

            if (session.State != before)
            {
                changed.Add(session);
            }
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(ct);

            foreach (var session in changed)
            {
                await notifier.PublishAsync(session, ct);
            }
        }

        return expired.Count;
    }

    private async Task DeliverAsync(CommandRecord command, SessionRecord? session, CancellationToken ct)
    {
        var message = new AgentCommand(command.Id, command.Type, command.ExpiresAt, JsonSerializer.Deserialize<JsonElement>(command.Payload));

        try
        {
            await hub.Clients.Group(AgentHub.GroupFor(command.AgentId)).SendAsync(command.Type, message, ct);
        }
        catch (Exception ex)
        {
            // Left Pending: it is resent on the agent's next connection, or expires.
            logger.LogWarning(ex, "Could not deliver command {CommandId} to agent {AgentId}", command.Id, command.AgentId);
            return;
        }

        command.Status = CommandStatuses.Sent;
        if (session is { State: SessionState.Requested } && command.Type == CommandTypes.StartScope)
        {
            session.State = SessionState.Starting;
        }

        await db.SaveChangesAsync(ct);
    }

    private static void Apply(CommandRecord command, SessionRecord session, DateTimeOffset now)
    {
        if (command.Type == CommandTypes.StartScope)
        {
            // A resync for a running session: confirm it, or flag it when the agent could not restore the scope.
            if (session.State is SessionState.Active or SessionState.Degraded)
            {
                var restored = command.Status == CommandStatuses.Succeeded;
                session.State = restored ? SessionState.Active : SessionState.Degraded;
                session.FailureReason = restored ? null : command.Error ?? "Agent could not restore the scope.";
                return;
            }

            // A start result that arrives after a stop was requested must not reactivate the session.
            if (session.State is not (SessionState.Requested or SessionState.Starting))
            {
                return;
            }

            if (command.Status == CommandStatuses.Succeeded)
            {
                session.State = SessionState.Active;
                session.ActiveAt = now;
            }
            else
            {
                Fail(session, command.Error ?? "Agent rejected the scope.", now);
            }
        }
        else if (command.Type == CommandTypes.StopScope && session.State == SessionState.Stopping)
        {
            if (command.Status == CommandStatuses.Succeeded)
            {
                session.State = SessionState.Completed;
                session.StoppedAt = now;
                session.FailureReason = null;
            }
            else
            {
                Fail(session, command.Error ?? "Agent could not stop the scope.", now);
            }
        }
    }

    private static void Fail(SessionRecord session, string reason, DateTimeOffset now)
    {
        session.State = SessionState.Failed;
        session.FailureReason = reason;
        session.StoppedAt = now;
    }
}
