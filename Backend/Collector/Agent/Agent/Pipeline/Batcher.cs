using Auditor.Agent.Outbox;

namespace Auditor.Agent.Pipeline;

// Groups actions into batches and writes them to the outbox. A batch is cut at maxActions, or when maxMs has
// passed since its first action. Read operations go to the ReadMerger and are written as separate read batches.
public sealed class Batcher(
    DiskOutbox outbox, int maxActions, TimeSpan maxAge, TimeProvider clock, ILogger? logger = null,
    Action<long, long?, long?, string>? onDropped = null)
{
    private const int MaxReadRowsPerBatch = 5000;

    private readonly List<AuditAction> _actions = [];
    private readonly ReadMerger _reads = new();
    private DateTimeOffset _openedAt;
    private DateTimeOffset _readsOpenedAt;

    // Actions lost because the outbox was full; the uploader's backlog must clear before new batches fit again.
    public long Dropped { get; private set; }

    public int Pending => _actions.Count + _reads.Pending;

    public void Add(AuditAction action)
    {
        if (ReadMerger.IsRead(action.Operation))
        {
            if (_reads.Pending == 0)
            {
                _readsOpenedAt = clock.GetUtcNow();
            }

            _reads.Add(action);
            return;
        }

        if (_actions.Count == 0)
        {
            _openedAt = clock.GetUtcNow();
        }

        _actions.Add(action);
        if (_actions.Count >= maxActions)
        {
            WriteActions();
        }
    }

    // Call periodically so a quiet stream still gets written; `force` writes everything now (shutdown).
    public void Tick(bool force = false)
    {
        var now = clock.GetUtcNow();
        if (_actions.Count > 0 && (force || now - _openedAt >= maxAge))
        {
            WriteActions();
        }

        if (_reads.Pending > 0 && (force || now - _readsOpenedAt >= maxAge))
        {
            WriteReads();
        }
    }

    private void WriteActions()
    {
        var batch = new OutboxBatch(OutboxBatch.ActionsKind, Guid.NewGuid(), _actions.ToList(), null);
        if (outbox.TryEnqueue(batch))
        {
            _actions.Clear();
            return;
        }

        Dropped += _actions.Count;
        onDropped?.Invoke(_actions.Count, _actions[0].Seq, _actions[^1].Seq, "outbox full");
        logger?.LogError("Outbox is full: dropped {Count} actions (seq {First}-{Last})", _actions.Count, _actions[0].Seq, _actions[^1].Seq);
        _actions.Clear();
    }

    private void WriteReads()
    {
        var rows = _reads.Drain();
        foreach (var chunk in rows.Chunk(MaxReadRowsPerBatch))
        {
            if (!outbox.TryEnqueue(new OutboxBatch(OutboxBatch.ReadsKind, Guid.NewGuid(), null, chunk.ToList())))
            {
                Dropped += chunk.Sum(r => r.ReadCount);
                onDropped?.Invoke(chunk.Sum(r => r.ReadCount), null, null, "outbox full (merged reads)");
                logger?.LogError("Outbox is full: dropped {Count} merged read rows", chunk.Length);
            }
        }
    }
}
