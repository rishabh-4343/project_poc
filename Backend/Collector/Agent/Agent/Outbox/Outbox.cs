using System.Security.Cryptography;
using System.Text.Json;
using Auditor.Agent.Pipeline;
using Auditor.Agent.Storage;

namespace Auditor.Agent.Outbox;

// What one outbox file holds. BatchId is fixed when the batch is created, so a resend after a crash carries the same id.
public sealed record OutboxBatch(string Kind, Guid BatchId, List<AuditAction>? Actions, List<ReadAction>? Reads)
{
    public const string ActionsKind = "actions";
    public const string ReadsKind = "reads";
}

// Durable queue of batches: one sealed file per batch, named by an increasing counter so the oldest uploads first.
// A file is removed only by Delete, which the uploader calls after the backend acknowledged the batch.
public sealed class DiskOutbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly LocalCrypto _crypto;
    private readonly long _maxBytes;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private long _counter;

    public DiskOutbox(string directory, LocalCrypto crypto, long maxBytes, ILogger? logger = null)
    {
        _directory = directory;
        _crypto = crypto;
        _maxBytes = maxBytes;
        _logger = logger;
        Directory.CreateDirectory(directory);

        foreach (var leftover in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            File.Delete(leftover);
        }

        _counter = Files().Select(f => long.Parse(Path.GetFileNameWithoutExtension(f))).DefaultIfEmpty(0).Max();
    }

    public int Count => Files().Count();

    public long Bytes => Files().Sum(f => new FileInfo(f).Length);

    // Returns false, writing nothing, when the size cap would be exceeded.
    public bool TryEnqueue(OutboxBatch batch)
    {
        var sealedData = _crypto.Seal(JsonSerializer.SerializeToUtf8Bytes(batch, Json));

        lock (_gate)
        {
            if (Bytes + sealedData.Length > _maxBytes)
            {
                return false;
            }

            var path = Path.Combine(_directory, $"{++_counter:D12}.batch");
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, sealedData);
            File.Move(temp, path);
            return true;
        }
    }

    // The oldest readable batch, or null. A file that cannot be opened (altered, or sealed with another key) is set aside.
    public (string Path, OutboxBatch Batch)? PeekOldest()
    {
        foreach (var path in Files())
        {
            try
            {
                var batch = JsonSerializer.Deserialize<OutboxBatch>(_crypto.Open(File.ReadAllBytes(path)), Json);
                if (batch is not null)
                {
                    return (path, batch);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                _logger?.LogError("Outbox file {File} is unreadable ({Reason}); moved aside", Path.GetFileName(path), ex.Message);
            }

            File.Move(path, path + ".corrupt", overwrite: true);
        }

        return null;
    }

    public void Delete(string path) => File.Delete(path);

    private IEnumerable<string> Files() => Directory.EnumerateFiles(_directory, "*.batch").Order(StringComparer.Ordinal);
}
