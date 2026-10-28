using Auditor.Agent.Pipeline;
using Auditor.Agent.Storage;

namespace Auditor.Agent.Driver;

// Reads records from the driver source, assigns the per-server seq, and hands them to the correlator.
public sealed class DriverReader(
    IDriverSource source, SeqStore seqs, PipelineChannels channels, AgentStats stats, IntegrityQueue integrity, ILogger<DriverReader> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("driver reader started, resuming after seq {Seq}", seqs.LastReserved);

        try
        {
            await foreach (var signal in source.ReadAsync(stoppingToken))
            {
                switch (signal)
                {
                    case DriverSignal.Record record:
                        await HandleAsync(record.Bytes, stoppingToken);
                        break;
                    case DriverSignal.Overflow overflow:
                        stats.DriverDropped = overflow.DroppedTotal;
                        integrity.Add(Auditor.Agent.Control.IntegrityKind.DriverOverflow, dropped: overflow.DroppedTotal);
                        logger.LogWarning("IntegrityEvent DriverOverflow: driver has dropped {Dropped} records", overflow.DroppedTotal);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            seqs.Release();
            logger.LogInformation("driver reader stopped");
        }
    }

    private async Task HandleAsync(byte[] bytes, CancellationToken ct)
    {
        RawEvent raw;
        try
        {
            raw = AuditRecordCodec.Parse(bytes);
        }
        catch (InvalidDataException ex)
        {
            // A driver/agent version mismatch must be loud, but must not stop the reader.
            logger.LogError("Rejected a driver record: {Reason}", ex.Message);
            return;
        }

        var ev = new SequencedEvent(seqs.Next(), raw);
        await channels.Events.Writer.WriteAsync(ev, ct);
    }
}
