using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;

namespace Auditor.Agent.Driver;

// Stands in for the kernel driver: emits frozen-format AUDIT_RECORD bytes on a timer so the pipeline can run end to end.
public sealed class SimulatedDriverSource(IOptions<AgentOptions> options) : IDriverSource
{
    private static readonly AuditOperation[] Cycle =
        [AuditOperation.Created, AuditOperation.Modified, AuditOperation.Renamed, AuditOperation.Deleted];

    public async IAsyncEnumerable<DriverSignal> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, options.Value.SimulatedIntervalMs));
        using var timer = new PeriodicTimer(interval);
        var n = 0;

        while (await timer.WaitForNextTickAsync(ct))
        {
            var op = Cycle[n % Cycle.Length];
            var file = $@"D:\Finance\report-{n / Cycle.Length}.xlsx";
            var renamed = op == AuditOperation.Renamed ? $@"D:\Finance\report-{n / Cycle.Length}-final.xlsx" : null;
            var denied = n % 10 == 9;

            yield return new DriverSignal.Record(Encode(
                op, AuditObjectType.File, denied ? AuditFlags.Denied : AuditFlags.None, 4242,
                denied ? unchecked((int)0xC0000022) : 0, DateTimeOffset.UtcNow, "S-1-5-21-1000", file, renamed));
            n++;
        }
    }

    // Writes one record exactly as laid out in audit_record.h (32-byte header, then sid, path, newPath as UTF-16LE).
    public static byte[] Encode(
        AuditOperation operation, AuditObjectType objectType, AuditFlags flags, uint processId, int status,
        DateTimeOffset timestamp, string sid, string path, string? newPath)
    {
        var sidBytes = Encoding.Unicode.GetBytes(sid);
        var pathBytes = Encoding.Unicode.GetBytes(path);
        var newPathBytes = newPath is null ? [] : Encoding.Unicode.GetBytes(newPath);

        var msg = new byte[AuditRecordCodec.HeaderSize + sidBytes.Length + pathBytes.Length + newPathBytes.Length];
        msg[0] = AuditRecordCodec.Version;
        msg[1] = (byte)operation;
        msg[2] = (byte)objectType;
        msg[3] = (byte)flags;
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(4, 4), processId);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(8, 4), status);
        BinaryPrimitives.WriteInt64LittleEndian(msg.AsSpan(16, 8), timestamp.ToFileTime());
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(24, 2), (ushort)sidBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(26, 2), (ushort)pathBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(msg.AsSpan(28, 2), (ushort)newPathBytes.Length);

        sidBytes.CopyTo(msg, AuditRecordCodec.HeaderSize);
        pathBytes.CopyTo(msg, AuditRecordCodec.HeaderSize + sidBytes.Length);
        newPathBytes.CopyTo(msg, AuditRecordCodec.HeaderSize + sidBytes.Length + pathBytes.Length);
        return msg;
    }
}
