namespace Auditor.Agent.Driver;

// What a driver connection yields: raw record bytes, or an overflow notice carrying the driver's running dropped total.
public abstract record DriverSignal
{
    public sealed record Record(byte[] Bytes) : DriverSignal;

    public sealed record Overflow(long DroppedTotal) : DriverSignal;
}

// The seam between the agent and the kernel driver. The real FilterConnectCommunicationPort source replaces the simulated one.
public interface IDriverSource
{
    IAsyncEnumerable<DriverSignal> ReadAsync(CancellationToken ct);
}

// A parsed event with the agent-assigned sequence number.
public sealed record SequencedEvent(long Seq, RawEvent Event);
