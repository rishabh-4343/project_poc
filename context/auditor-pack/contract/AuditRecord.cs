// =====================================================================
// AuditRecord.cs — C# mirror of audit_record.h (SHARED, FROZEN).
// Must match the header byte-for-byte. Change only with the header, in
// lockstep, bumping Version. Little-endian; paths UTF-16LE.
// =====================================================================
using System.Buffers.Binary;
using System.Text;

namespace Auditor.Agent.Driver;

public enum AuditOperation : byte
{
    Created = 1, Modified = 2, Deleted = 3, Renamed = 4, Moved = 5, Copied = 6,
    Open = 7, Read = 8, ContentView = 9, ReadBasicInfo = 10, PropertyChanged = 11,
    PermissionChange = 12, OwnerChange = 13, AuditingChange = 14,
    ShareCreated = 15, ShareRemoved = 16, Other = 99
}

public enum AuditObjectType : byte { File = 1, Folder = 2, Share = 3 }

[Flags] public enum AuditFlags : byte { None = 0, Denied = 0x01, Inferred = 0x02 }

/// One decoded record from the driver. 'seq' is assigned by the agent, not on the wire.
public sealed record RawEvent(
    byte Version, AuditOperation Operation, AuditObjectType ObjectType, AuditFlags Flags,
    uint ProcessId, int Status, DateTimeOffset Timestamp,
    string Sid, string Path, string? NewPath)
{
    public bool Success => (Flags & AuditFlags.Denied) == 0;
}

public static class AuditRecordCodec
{
    public const byte Version = 1;
    public const int HeaderSize = 32;

    /// Parse one message (header + variable region) exactly as laid out in audit_record.h.
    public static RawEvent Parse(ReadOnlySpan<byte> msg)
    {
        if (msg.Length < HeaderSize)
            throw new InvalidDataException($"record too short: {msg.Length} < {HeaderSize}");

        byte version    = msg[0];
        var  operation  = (AuditOperation)msg[1];
        var  objectType = (AuditObjectType)msg[2];
        var  flags      = (AuditFlags)msg[3];
        uint processId  = BinaryPrimitives.ReadUInt32LittleEndian(msg.Slice(4, 4));
        int  status     = BinaryPrimitives.ReadInt32LittleEndian(msg.Slice(8, 4));
        // bytes 12..16 reserved
        long fileTime   = BinaryPrimitives.ReadInt64LittleEndian(msg.Slice(16, 8));
        int  sidLen     = BinaryPrimitives.ReadUInt16LittleEndian(msg.Slice(24, 2));
        int  pathLen    = BinaryPrimitives.ReadUInt16LittleEndian(msg.Slice(26, 2));
        int  newPathLen = BinaryPrimitives.ReadUInt16LittleEndian(msg.Slice(28, 2));
        // bytes 30..32 reserved

        int need = HeaderSize + sidLen + pathLen + newPathLen;
        if (msg.Length < need)
            throw new InvalidDataException($"record truncated: have {msg.Length}, need {need}");

        if (version != Version)
            throw new InvalidDataException($"record version {version} != supported {Version}");

        int o = HeaderSize;
        string sid     = Utf16(msg.Slice(o, sidLen)); o += sidLen;
        string path    = Utf16(msg.Slice(o, pathLen)); o += pathLen;
        string? newPath = newPathLen > 0 ? Utf16(msg.Slice(o, newPathLen)) : null;

        // FILETIME (100ns ticks since 1601-01-01 UTC) -> DateTimeOffset
        var ts = DateTimeOffset.FromFileTime(fileTime);

        return new RawEvent(version, operation, objectType, flags, processId, status, ts, sid, path, newPath);
    }

    private static string Utf16(ReadOnlySpan<byte> bytes) => Encoding.Unicode.GetString(bytes);
}
