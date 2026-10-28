using System.Text.Json;
using Auditor.Agent.Pipeline;

namespace Auditor.Agent.Control;

// Wire shapes for /hubs/agent. They must match the backend's contracts (camelCase JSON, enums as strings); see its NOTES.md.

// Pushed by the backend as the client method StartScope or StopScope.
public sealed record AgentCommand(Guid CommandId, string Type, DateTimeOffset ExpiresAt, JsonElement Payload);

public sealed record CommandResultMessage(Guid CommandId, bool Success, string? Error, JsonElement? Result);

public sealed record StopScopeCommand(Guid SessionId);

public sealed record ActionBatch(Guid ServerId, List<AuditAction> Actions);

public sealed record ReadBatch(Guid BatchId, Guid ServerId, List<ReadAction> Reads);

public sealed record BatchAck(Guid ServerId, long LastSeq, int Stored, int Duplicates);

public sealed record ServerHeartbeat(Guid ServerId, long DroppedCount, long RingLag, long OutboxSize, int ActiveScopes);

public sealed record HeartbeatMessage(string? AgentVersion, string? DriverVersion, List<ServerHeartbeat> Servers);

public enum IntegrityKind { DriverOverflow, Gap, GapRecovered, AgentOffline, DriverUnloaded, TamperSuspected }

public sealed record IntegrityEventMessage(
    Guid ServerId, IntegrityKind Kind, DateTimeOffset StartedAt, DateTimeOffset? EndedAt,
    long? FirstSeq, long? LastSeq, long? DroppedCount, string? Details);

public sealed record ShareInfo(string Name, string LocalPath);

// Pushed by the backend as the client method UpdateShares: the full share list of one server.
public sealed record UpdateSharesMessage(Guid ServerId, List<ShareInfo> Shares);
