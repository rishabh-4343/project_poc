using Auditor.Agent.Control;
using Auditor.Agent.Outbox;
using Auditor.Agent.Pipeline;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Auditor.Agent.Workers;

// Drains the outbox oldest-first. A file is deleted only after the backend acknowledged the batch, so a crash or an
// outage never loses data; the backend deduplicates a resend. Integrity events are sent ahead of each batch.
public sealed class UploadWorker(
    AgentChannel channel, DiskOutbox outbox, IntegrityQueue integrity, IOptions<AgentOptions> options, ILogger<UploadWorker> logger)
    : BackgroundService
{
    // A batch the backend keeps refusing is set aside after this many refusals so it cannot block the queue.
    private const int MaxRefusals = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var refusals = new Dictionary<string, int>();
        var backoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!channel.IsConnected)
                {
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                await SendIntegrityAsync(o.ServerId, stoppingToken);

                var next = outbox.PeekOldest();
                if (next is null)
                {
                    await Task.Delay(200, stoppingToken);
                    continue;
                }

                var (path, batch) = next.Value;
                try
                {
                    var ack = batch.Kind == OutboxBatch.ReadsKind
                        ? await channel.InvokeAsync<BatchAck>("ReadBatch", new ReadBatch(batch.BatchId, o.ServerId, batch.Reads!), stoppingToken)
                        : await channel.InvokeAsync<BatchAck>("EventBatch", new ActionBatch(o.ServerId, batch.Actions!), stoppingToken);

                    outbox.Delete(path);
                    refusals.Remove(path);
                    backoff = TimeSpan.FromSeconds(1);
                    logger.LogInformation(
                        "uploaded {Kind} batch {BatchId}: stored {Stored}, duplicates {Duplicates}, {Backlog} left",
                        batch.Kind, batch.BatchId, ack.Stored, ack.Duplicates, outbox.Count);
                }
                catch (HubException ex)
                {
                    // The backend understood the batch and refused it. Retrying rarely helps, but do not drop it at once.
                    var count = refusals.GetValueOrDefault(path) + 1;
                    refusals[path] = count;
                    logger.LogError("Backend refused {File} ({Count}/{Max}): {Reason}", Path.GetFileName(path), count, MaxRefusals, ex.Message);

                    if (count >= MaxRefusals)
                    {
                        File.Move(path, path + ".rejected", overwrite: true);
                        refusals.Remove(path);
                    }

                    await Task.Delay(backoff, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A dropped connection or timeout: keep the file and try again.
                logger.LogWarning("Upload failed ({Reason}); retrying in {Delay}s", ex.Message, backoff.TotalSeconds);
                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }

    private async Task SendIntegrityAsync(Guid serverId, CancellationToken ct)
    {
        while (integrity.TryTake(out var item) && item is not null)
        {
            try
            {
                await channel.InvokeAsync(
                    "IntegrityEvent",
                    new IntegrityEventMessage(serverId, item.Kind, item.At, null, item.FirstSeq, item.LastSeq, item.Dropped, item.Details),
                    ct);
            }
            catch (HubException ex)
            {
                logger.LogError("Backend refused an integrity event ({Kind}): {Reason}", item.Kind, ex.Message);
            }
            catch
            {
                integrity.Return(item);
                throw;
            }
        }
    }
}
