using System.Buffers.Binary;
using System.Text;
using Auditor.Agent.Pipeline;

namespace Auditor.Agent.Driver;

// C# mirror of Backend/Driver/AuditorDriver/driver_protocol.h (the driver<->agent CONTROL protocol;
// audit_record.h is mirrored separately in AuditRecord.cs). Keep in lockstep with that header.
// Little-endian; paths are UTF-16LE NT paths (\Device\HarddiskVolumeN\...). The agent translates
// to/from drive-letter paths with NtPathMap.
public static class DriverProtocol
{
    public const uint Version = 1;
    public const string PortName = @"\AuditorDriverPort";

    public const byte NoticeMarker = 0xF0;   // first byte of a control notice (can never be a record version)
    public const byte NoticeOverflow = 1;

    public const uint CmdSetScope = 1;
    public const uint CmdGetStats = 2;

    public const ushort FolderRecursive = 0x0001;
    public const int MaxFolderBytes = 65534;
    public const int ReplySize = 32;          // sizeof(AUDITOR_REPLY)
    public const int OverflowNoticeSize = 16; // sizeof(AUDITOR_OVERFLOW_NOTICE)

    // AUDIT_OP_BIT(op): bit (op-1) for operations 1..16, bit 31 for OP_OTHER (99).
    public static uint OpBit(AuditOperation op) =>
        op == AuditOperation.Other ? 0x8000_0000u : (1u << (((byte)op - 1) & 31));

    public static uint OperationMask(IEnumerable<AuditOperation> ops)
    {
        uint m = 0;
        foreach (var op in ops) m |= OpBit(op);
        return m;
    }

    // SET_SCOPE payload: [command(4)][protoVer(4)][operationMask(4)][folderCount(4)]
    //   then folderCount * ( [flags(2)][pathBytes(2)][NT path, UTF-16LE, no terminator] ).
    // Folders whose drive letter cannot be resolved to an NT device are dropped; the count in the
    // driver's reply will be lower than scope.Folders.Count, which the caller logs.
    public static byte[] EncodeSetScope(DriverScope scope, NtPathMap paths)
    {
        var folders = new List<(ushort flags, byte[] path)>();
        foreach (var f in scope.Folders)
        {
            var nt = paths.ToNt(f.LocalPath);
            if (nt is null) continue;
            var bytes = Encoding.Unicode.GetBytes(nt);
            if (bytes.Length is 0 or > MaxFolderBytes) continue;
            folders.Add((f.Recursive ? FolderRecursive : (ushort)0, bytes));
        }

        int size = 16 + folders.Sum(x => 4 + x.path.Length);
        var buf = new byte[size];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s[0..], CmdSetScope);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], OperationMask(scope.Operations));
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)folders.Count);
        int o = 16;
        foreach (var (flags, path) in folders)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[o..], flags); o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)path.Length); o += 2;
            path.CopyTo(buf, o); o += path.Length;
        }
        return buf;
    }

    public static byte[] EncodeGetStats()
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), CmdGetStats);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), Version);
        return buf;
    }

    public readonly record struct Reply(uint ProtocolVersion, uint FolderCount, uint OperationMask, ulong DroppedTotal, ulong SentTotal);

    public static Reply ParseReply(ReadOnlySpan<byte> b, int len)
    {
        if (len < ReplySize || b.Length < ReplySize) return default;
        return new Reply(
            BinaryPrimitives.ReadUInt32LittleEndian(b[0..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(b[24..]));
    }

    // Overflow notice: [marker(1)=0xF0][kind(1)][reserved(2)][protoVer(4)][droppedTotal(8)].
    public static long ParseOverflowDropped(ReadOnlySpan<byte> payload) =>
        (long)BinaryPrimitives.ReadUInt64LittleEndian(payload[8..16]);
}
