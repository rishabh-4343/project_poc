using server.Modules.Wfsa.Data;

namespace server.Modules.Wfsa.Ingestion;

public enum ObjectType { File, Folder, Share }

public enum Certainty { Certain, Inferred }

// Agent -> backend shape for one normalized action; mirrors the agent guide's AuditAction.
public sealed record AuditAction(
    Guid TenantId, Guid ServerId, long Seq, DateTimeOffset OccurredAt,
    ObjectType ObjectType, Operation Operation, bool Success, Certainty Certainty,
    string Path, long PathHash, string? NewPath, string? ShareName,
    string ActorSid, string? ActorName, string? ClientIp, string? ProcessName,
    string? OldSecurity, string? NewSecurity, string? Details);

public sealed record ActionBatch(Guid ServerId, List<AuditAction> Actions);

// One row per (server, actorSid, pathHash, hour), already merged by the agent.
public sealed record ReadAction(
    DateTimeOffset BucketStart, string ActorSid, long PathHash, DateTimeOffset FirstAt, DateTimeOffset LastAt,
    uint ReadCount, ObjectType ObjectType, string Path, string? ActorName, string? ClientIp, string? ProcessName,
    bool Success = true);

// BatchId is chosen by the agent and must stay the same when the batch is resent.
public sealed record ReadActionBatch(Guid BatchId, Guid ServerId, List<ReadAction> Reads);

public sealed record BatchAck(Guid ServerId, long LastSeq, int Stored, int Duplicates);

// Per-server health inside an agent heartbeat; DroppedCount is the driver's running total.
public sealed record ServerHeartbeat(Guid ServerId, long DroppedCount, long RingLag, long OutboxSize, int ActiveScopes);

public sealed record HeartbeatMessage(string? AgentVersion, string? DriverVersion, List<ServerHeartbeat>? Servers);

public enum IntegrityKind { DriverOverflow, Gap, GapRecovered, AgentOffline, DriverUnloaded, TamperSuspected }

// A resend with the same (ServerId, Kind, StartedAt) updates the existing event instead of adding another.
public sealed record IntegrityEventMessage(
    Guid ServerId, IntegrityKind Kind, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    long? FirstSeq, long? LastSeq, long? DroppedCount, string? Details);
