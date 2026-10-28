using System.Threading.Channels;
using Auditor.Agent.Control;

namespace Auditor.Agent.Pipeline;

// Counters shared by the pipeline stages and reported in the heartbeat.
public sealed class AgentStats
{
    private long _driverDropped;
    private long _outboxDropped;

    // The driver's running total of records it could not queue.
    public long DriverDropped
    {
        get => Interlocked.Read(ref _driverDropped);
        set => Interlocked.Exchange(ref _driverDropped, value);
    }

    // Actions this agent threw away because the outbox was full.
    public long OutboxDropped => Interlocked.Read(ref _outboxDropped);

    public void AddOutboxDropped(long count) => Interlocked.Add(ref _outboxDropped, count);

    public long DroppedTotal => DriverDropped + OutboxDropped;
}

public sealed record PendingIntegrity(
    IntegrityKind Kind, DateTimeOffset At, long? FirstSeq, long? LastSeq, long? Dropped, string? Details);

// Integrity events waiting to be sent. They are produced anywhere in the pipeline and drained by the uploader.
public sealed class IntegrityQueue
{
    private readonly Channel<PendingIntegrity> _queue = Channel.CreateUnbounded<PendingIntegrity>();

    public void Add(IntegrityKind kind, long? firstSeq = null, long? lastSeq = null, long? dropped = null, string? details = null) =>
        _queue.Writer.TryWrite(new PendingIntegrity(kind, DateTimeOffset.UtcNow, firstSeq, lastSeq, dropped, details));

    public bool TryTake(out PendingIntegrity? item) => _queue.Reader.TryRead(out item);

    // Puts an event back after a failed send so it is not lost.
    public void Return(PendingIntegrity item) => _queue.Writer.TryWrite(item);
}
