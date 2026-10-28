using Microsoft.Extensions.Options;

namespace Auditor.Agent.Pipeline;

// Runs the Correlator between the driver reader and the next stage, flushing held actions on a timer.
public sealed class CorrelatorWorker(
    PipelineChannels channels, Enricher enricher, Router router, IOptions<AgentOptions> options, ILogger<CorrelatorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var correlator = new Correlator(TimeSpan.FromSeconds(options.Value.ModifyMergeSeconds));
        var reader = channels.Events.Reader;
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(500));

        try
        {
            var pending = reader.ReadAsync(stoppingToken).AsTask();
            var tickTask = tick.WaitForNextTickAsync(stoppingToken).AsTask();

            while (true)
            {
                var done = await Task.WhenAny(pending, tickTask);
                if (done == tickTask)
                {
                    await EmitAsync(correlator.Flush(DateTimeOffset.UtcNow), stoppingToken);
                    tickTask = tick.WaitForNextTickAsync(stoppingToken).AsTask();
                    continue;
                }

                await EmitAsync(correlator.Process(await pending), stoppingToken);
                pending = reader.ReadAsync(stoppingToken).AsTask();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Nothing is lost on a clean stop; A6's outbox will persist these.
            await EmitAsync(correlator.FlushAll(), CancellationToken.None);
        }
    }

    private async Task EmitAsync(List<CorrelatedAction> actions, CancellationToken ct)
    {
        foreach (var action in actions)
        {
            var a = enricher.Enrich(action);
            if (!router.Accept(a))
            {
                continue;
            }

            await channels.Actions.Writer.WriteAsync(a, ct);
            logger.LogInformation(
                "action seq {Seq} {Operation} {Path}{NewPath} by {Actor} ({Sid}) via {Process} hash {Hash}{Details} {Result}",
                a.Seq, a.Operation, a.Path, a.NewPath is null ? "" : " -> " + a.NewPath, a.ActorName ?? "?", a.ActorSid,
                a.ProcessName ?? "?", a.PathHash, a.Details is null ? "" : $" [{a.Details}]", a.Success ? "ok" : "DENIED");
        }
    }
}
