using System.Text.Json.Serialization;
using Auditor.Agent.Pipeline;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace Auditor.Agent.Control;

// The outbound connection to the backend's /hubs/agent. It reconnects forever; on every (re)connect the backend
// resends unanswered commands and the scope of each running session.
public sealed class AgentChannel : BackgroundService
{
    private readonly HubConnection _connection;
    private readonly ILogger<AgentChannel> _logger;

    public AgentChannel(IOptions<AgentOptions> options, CommandHandler handler, ShareMap shareMap, ILogger<AgentChannel> logger)
    {
        _logger = logger;
        var o = options.Value;

        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(o.BackendUrl), "/hubs/agent"), http => http.Headers["X-Agent-Key"] = o.ApiKey)
            .AddJsonProtocol(p => p.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        // The backend invokes the client method named after the command type.
        foreach (var type in new[] { "StartScope", "StopScope" })
        {
            _connection.On<AgentCommand>(type, async command =>
            {
                var result = handler.Handle(command);
                try
                {
                    await _connection.InvokeAsync("CommandResult", result);
                }
                catch (Exception ex)
                {
                    // Unanswered, the backend resends the command and the handler repeats this answer.
                    _logger.LogWarning(ex, "Could not send the result for command {CommandId}", command.CommandId);
                }
            });
        }

        _connection.On<UpdateSharesMessage>("UpdateShares", message =>
        {
            if (message.ServerId != o.ServerId)
            {
                return;
            }

            shareMap.Set(message.Shares.Select(s => (s.Name, s.LocalPath)));
            _logger.LogInformation("Shares updated: {Count}", message.Shares.Count);
        });

        _connection.Reconnecting += ex =>
        {
            _logger.LogWarning("Connection lost: {Reason}", ex?.Message);
            return Task.CompletedTask;
        };
        _connection.Reconnected += _ =>
        {
            _logger.LogInformation("Reconnected to the backend");
            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    public Task<T> InvokeAsync<T>(string method, object argument, CancellationToken ct) =>
        _connection.InvokeAsync<T>(method, argument, ct);

    public Task InvokeAsync(string method, object argument, CancellationToken ct) =>
        _connection.InvokeAsync(method, argument, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _connection.StartAsync(stoppingToken);
                    _logger.LogInformation("Connected to the backend");
                    break;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Backend not reachable ({Reason}); retrying in {Delay}s", ex.Message, delay.TotalSeconds);
                    await Task.Delay(delay, stoppingToken);
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                }
            }

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await _connection.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(retryContext.PreviousRetryCount, 5))));
    }
}
