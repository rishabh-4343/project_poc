using Auditor.Agent.Outbox;
using Microsoft.Extensions.Options;

namespace Auditor.Agent.Pipeline;

// Moves routed actions into the Batcher, which writes them to the outbox.
public sealed class BatcherWorker(
    PipelineChannels channels, DiskOutbox outbox, AgentStats stats, IntegrityQueue integrity, IOptions<AgentOptions> options,
    TimeProvider clock, ILogger<BatcherWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var batcher = new Batcher(outbox, o.BatchMaxActions, TimeSpan.FromMilliseconds(o.BatchMaxMs), clock, logger, (count, first, last, why) =>
        {
            stats.AddOutboxDropped(count);
            integrity.Add(Auditor.Agent.Control.IntegrityKind.Gap, first, last, count, why);
        });
        var reader = channels.Actions.Reader;
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(50, o.BatchMaxMs / 4)));

        logger.LogInformation("batcher started: {Backlog} batch(es) already in the outbox", outbox.Count);

        try
        {
            var pending = reader.ReadAsync(stoppingToken).AsTask();
            var tickTask = tick.WaitForNextTickAsync(stoppingToken).AsTask();

            while (true)
            {
                var done = await Task.WhenAny(pending, tickTask);
                if (done == tickTask)
                {
                    batcher.Tick();
                    tickTask = tick.WaitForNextTickAsync(stoppingToken).AsTask();
                    continue;
                }

                batcher.Add(await pending);
                pending = reader.ReadAsync(stoppingToken).AsTask();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Drain what is already queued, then write it all so a clean stop loses nothing.
            while (reader.TryRead(out var action))
            {
                batcher.Add(action);
            }

            batcher.Tick(force: true);
            logger.LogInformation("batcher stopped: {Backlog} batch(es) in the outbox", outbox.Count);
        }
    }
}
