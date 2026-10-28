using Auditor.Agent.Driver;

namespace Auditor.Agent.Pipeline;

// Agent -> backend shape of one merged read row; names must match the backend's ReadAction.
public sealed record ReadAction(
    DateTimeOffset BucketStart, string ActorSid, long PathHash, DateTimeOffset FirstAt, DateTimeOffset LastAt,
    uint ReadCount, AuditObjectType ObjectType, string Path, string? ActorName, string? ClientIp, string? ProcessName,
    bool Success = true);

// Reads and content views are far too frequent to send one by one: they are merged into one row per
// (hour, user, path, outcome) with a count. Each Drain hands over the counts gathered since the last one,
// and the backend sums them.
public sealed class ReadMerger
{
    public static bool IsRead(AuditOperation op) => op is AuditOperation.Read or AuditOperation.ContentView;

    private readonly Dictionary<(DateTimeOffset Bucket, string Sid, long PathHash, bool Success), ReadAction> _rows = new();

    public int Pending => _rows.Count;

    public void Add(AuditAction a)
    {
        var at = a.OccurredAt.ToUniversalTime();
        var bucket = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, 0, 0, TimeSpan.Zero);
        var key = (bucket, a.ActorSid, a.PathHash, a.Success);

        _rows[key] = _rows.TryGetValue(key, out var row)
            ? row with { ReadCount = row.ReadCount + 1, FirstAt = at < row.FirstAt ? at : row.FirstAt, LastAt = at > row.LastAt ? at : row.LastAt }
            : new ReadAction(bucket, a.ActorSid, a.PathHash, at, at, 1, a.ObjectType, a.Path, a.ActorName, a.ClientIp, a.ProcessName, a.Success);
    }

    public List<ReadAction> Drain()
    {
        var rows = _rows.Values.OrderBy(r => r.BucketStart).ThenBy(r => r.FirstAt).ToList();
        _rows.Clear();
        return rows;
    }
}
