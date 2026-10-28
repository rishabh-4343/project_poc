using Auditor.Agent.Driver;

namespace Auditor.Agent.Pipeline;

// One normalized action. Seq is the seq of the first event it was built from; MergedCount is how many writes it covers.
public sealed record CorrelatedAction(long Seq, RawEvent Event, int MergedCount);

// Turns the driver's events into actions. Time is taken from event timestamps (and the `now` passed to Flush),
// never the clock, so behaviour is deterministic.
//  - Repeated successful Modified on the same (sid, path) within `window` become one action.
//  - A repeated Deleted for the same (sid, path) within `window` (disposition then cleanup) becomes one action.
//  - A Renamed/Moved/Deleted of a path first releases that path's held Modified, so order is preserved.
//  - Everything else passes through unchanged.
public sealed class Correlator(TimeSpan window)
{
    private sealed class Held(SequencedEvent first)
    {
        public SequencedEvent First { get; } = first;
        public DateTimeOffset LastAt { get; set; } = first.Event.Timestamp;
        public int Count { get; set; } = 1;
    }

    private readonly Dictionary<(string Sid, string Path), Held> _held = new();
    private readonly Dictionary<(string Sid, string Path), DateTimeOffset> _recentDeletes = new();
    private long _maxSeq;

    // Every seq <= SafeSeq has been fully emitted, so it is safe to checkpoint. Held events keep it below their own seq.
    public long SafeSeq => _held.Count > 0 ? _held.Values.Min(h => h.First.Seq) - 1 : _maxSeq;

    public List<CorrelatedAction> Process(SequencedEvent ev)
    {
        var output = new List<CorrelatedAction>();
        var raw = ev.Event;
        var key = (raw.Sid, raw.Path);

        Release(output, raw.Timestamp);
        _maxSeq = Math.Max(_maxSeq, ev.Seq);

        if (raw.Operation == AuditOperation.Modified && raw.Success)
        {
            if (_held.TryGetValue(key, out var held))
            {
                held.LastAt = raw.Timestamp;
                held.Count++;
            }
            else
            {
                _held[key] = new Held(ev);
            }

            return output;
        }

        if (raw.Operation is AuditOperation.Renamed or AuditOperation.Moved or AuditOperation.Deleted)
        {
            ReleaseKey(output, key);
        }

        if (raw.Operation == AuditOperation.Deleted && raw.Success)
        {
            if (_recentDeletes.TryGetValue(key, out var at) && raw.Timestamp - at <= window)
            {
                _recentDeletes[key] = raw.Timestamp;
                return output;
            }

            _recentDeletes[key] = raw.Timestamp;
        }

        output.Add(new CorrelatedAction(ev.Seq, raw, 1));
        return output;
    }

    // Emits held actions whose merge window has passed at `now`; call it on a timer so a quiet file is not held forever.
    public List<CorrelatedAction> Flush(DateTimeOffset now)
    {
        var output = new List<CorrelatedAction>();
        Release(output, now);
        return output;
    }

    // Emits everything still held, for shutdown.
    public List<CorrelatedAction> FlushAll()
    {
        var output = new List<CorrelatedAction>();
        foreach (var key in _held.Keys.ToList())
        {
            ReleaseKey(output, key);
        }

        return output;
    }

    private void Release(List<CorrelatedAction> output, DateTimeOffset now)
    {
        foreach (var (key, held) in _held.Where(kv => now - kv.Value.LastAt > window).OrderBy(kv => kv.Value.First.Seq).ToList())
        {
            ReleaseKey(output, key);
        }

        foreach (var key in _recentDeletes.Where(kv => now - kv.Value > window).Select(kv => kv.Key).ToList())
        {
            _recentDeletes.Remove(key);
        }
    }

    private void ReleaseKey(List<CorrelatedAction> output, (string Sid, string Path) key)
    {
        if (_held.Remove(key, out var held))
        {
            output.Add(new CorrelatedAction(held.First.Seq, held.First.Event, held.Count));
        }
    }
}
