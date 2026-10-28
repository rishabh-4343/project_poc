using Auditor.Agent.Driver;

namespace Auditor.Agent.Pipeline;

public enum Certainty { Certain, Inferred }

// Agent -> backend shape of one action; field names and enum names must match the backend's AuditAction (sent as camelCase JSON, enums as strings).
public sealed record AuditAction(
    Guid TenantId, Guid ServerId, long Seq, DateTimeOffset OccurredAt,
    AuditObjectType ObjectType, AuditOperation Operation, bool Success, Certainty Certainty,
    string Path, long PathHash, string? NewPath, string? ShareName,
    string ActorSid, string? ActorName, string? ClientIp, string? ProcessName,
    string? OldSecurity, string? NewSecurity, string? Details);
