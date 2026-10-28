using System.Reflection;
using Auditor.Agent.Control;
using Auditor.Agent.Outbox;
using Auditor.Agent.Pipeline;
using Microsoft.Extensions.Options;

namespace Auditor.Agent.Workers;

// Reports agent health every HeartbeatSeconds while connected: versions, dropped total, outbox size, active scopes.
public sealed class HeartbeatWorker(
    AgentChannel channel, DiskOutbox outbox, ScopeManager scopes, AgentStats stats, IOptions<AgentOptions> options,
    ILogger<HeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, o.HeartbeatSeconds)));

        try
        {
            do
            {
                if (!channel.IsConnected || o.ServerId == Guid.Empty)
                {
                    continue;
                }

                try
                {
                    var server = new ServerHeartbeat(o.ServerId, stats.DroppedTotal, 0, outbox.Count, scopes.Count);
                    await channel.InvokeAsync("Heartbeat", new HeartbeatMessage(version, null, [server]), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Heartbeat failed: {Reason}", ex.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
