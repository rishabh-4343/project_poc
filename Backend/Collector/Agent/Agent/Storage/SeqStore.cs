namespace Auditor.Agent.Storage;

// Persists the per-server seq high-water mark in the encrypted LocalStore.
// Seqs are reserved in blocks: the persisted value is always >= any seq handed out, so after a crash the next
// seq is above everything already used. A crash may leave a gap in seq, never a reused number.
public sealed class SeqStore
{
    public const int BlockSize = 1000;

    private const string Name = "seq";

    private readonly LocalStore _store;
    private readonly object _gate = new();
    private long _next;
    private long _reservedUpTo;

    public SeqStore(LocalStore store)
    {
        _store = store;
        _reservedUpTo = store.Read<State>(Name)?.ReservedUpTo ?? 0;
        _next = _reservedUpTo;
    }

    public long LastReserved => _reservedUpTo;

    public long Next()
    {
        lock (_gate)
        {
            if (_next >= _reservedUpTo)
            {
                Reserve(_reservedUpTo + BlockSize);
            }

            return ++_next;
        }
    }

    // On clean shutdown, give back the unused part of the block so the restart continues without a gap.
    public void Release()
    {
        lock (_gate)
        {
            Reserve(_next);
        }
    }

    private void Reserve(long upTo)
    {
        _store.Write(Name, new State(upTo));
        _reservedUpTo = upTo;
    }

    private sealed record State(long ReservedUpTo);
}
