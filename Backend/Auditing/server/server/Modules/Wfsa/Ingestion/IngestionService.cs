using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using server.Common.Auth;
using server.Common.Ch;
using server.Common.Hubs;
using server.Modules.Wfsa.Data;
using server.Modules.Wfsa.Reports;

namespace server.Modules.Wfsa.Ingestion;

public sealed class IngestionService(
    WfsaDbContext db, ClickHouseConnectionFactory factory, ClickHouseBulkWriter writer, IMemoryCache cache,
    IHubContext<UiHub> ui, ILogger<IngestionService> logger)
{
    public const int MaxBatchItems = 10_000;

    private static readonly string[] ActionColumns =
    [
        "tenant_id", "server_id", "seq", "occurred_at", "object_type", "operation", "success", "certainty",
        "path", "path_hash", "new_path", "share_name", "actor_sid", "actor_name", "client_ip", "process_name",
        "old_security", "new_security", "content_type", "risk_level", "compliance_tags", "monetary_value", "details",
    ];

    private static readonly string[] ReadColumns =
    [
        "tenant_id", "server_id", "bucket_start", "actor_sid", "path_hash", "first_at", "last_at", "read_count",
        "object_type", "path", "actor_name", "client_ip", "process_name", "success",
        "content_type", "risk_level", "compliance_tags", "monetary_value",
    ];

    public async Task<BatchAck> IngestActionsAsync(AgentIdentity agent, ActionBatch batch, CancellationToken ct)
    {
        var items = batch.Actions ?? [];
        if (items.Count == 0)
        {
            return new BatchAck(batch.ServerId, 0, 0, 0);
        }

        if (items.Count > MaxBatchItems)
        {
            throw new HubException($"Batch exceeds {MaxBatchItems} actions.");
        }

        await EnsureServerAsync(agent, batch.ServerId, ct);

        foreach (var a in items)
        {
            if (a.ServerId != batch.ServerId || (a.TenantId != Guid.Empty && a.TenantId != agent.TenantId) || a.Seq < 0)
            {
                throw new HubException($"Action seq {a.Seq} does not belong to this agent's tenant/server.");
            }
        }

        var actions = items.GroupBy(a => a.Seq).Select(g => g.Last()).ToList();
        var maxSeq = actions.Max(a => a.Seq);

        var existing = await ExistingSeqsAsync(agent.TenantId, batch.ServerId, actions, ct);
        var fresh = actions.Where(a => !existing.Contains((ulong)a.Seq)).ToList();

        var ddc = new Dictionary<long, FileClassificationRef>();
        if (fresh.Count > 0)
        {
            ddc = await LoadClassificationsAsync(agent.TenantId, batch.ServerId, fresh.Select(a => a.PathHash), ct);
            var rows = fresh.Select(a =>
            {
                ddc.TryGetValue(a.PathHash, out var c);
                return new object[]
                {
                    agent.TenantId, a.ServerId, (ulong)a.Seq, a.OccurredAt.UtcDateTime,
                    a.ObjectType.ToString(), a.Operation.ToString(), a.Success, a.Certainty.ToString(),
                    a.Path ?? "", unchecked((ulong)a.PathHash), a.NewPath ?? "", a.ShareName ?? "",
                    a.ActorSid ?? "", a.ActorName ?? "", a.ClientIp ?? "", a.ProcessName ?? "",
                    a.OldSecurity ?? "", a.NewSecurity ?? "",
                    c?.ContentType ?? "", (sbyte)(c?.RiskLevel ?? -1), c?.ComplianceTags ?? [], c?.MonetaryValue ?? 0m,
                    a.Details ?? "",
                };
            });
            await writer.WriteAsync("wfsa.actions", ActionColumns, rows, ct);
        }

        // Storage is durable before the position moves; a failure here only causes a deduplicated resend.
        var lastEventAt = actions.Max(a => a.OccurredAt).ToUniversalTime();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wfsa.agent_positions (tenant_id, server_id, agent_id, last_seq, last_event_at, updated_at)
            VALUES ({agent.TenantId}, {batch.ServerId}, {agent.AgentId}, {maxSeq}, {lastEventAt}, now())
            ON CONFLICT (server_id) DO UPDATE SET
                last_seq = GREATEST(wfsa.agent_positions.last_seq, EXCLUDED.last_seq),
                last_event_at = GREATEST(wfsa.agent_positions.last_event_at, EXCLUDED.last_event_at),
                agent_id = EXCLUDED.agent_id,
                updated_at = now()
            """, ct);

        if (fresh.Count > 0)
        {
            await PushToSessionsAsync(agent.TenantId, batch.ServerId, fresh, ddc, ct);
        }

        return new BatchAck(batch.ServerId, maxSeq, fresh.Count, actions.Count - fresh.Count);
    }

    // Only newly stored rows are pushed, and only to sessions whose scope contains them. A push failure never fails the ack.
    private async Task PushToSessionsAsync(
        Guid tenantId, Guid serverId, List<AuditAction> fresh, Dictionary<long, FileClassificationRef> ddc, CancellationToken ct)
    {
        try
        {
            var sessions = await db.Sessions.AsNoTracking().Include(s => s.Folders)
                .Where(s => s.TenantId == tenantId && s.ServerId == serverId
                    && (s.State == SessionState.Active || s.State == SessionState.Degraded || s.State == SessionState.Stopping))
                .ToListAsync(ct);

            foreach (var session in sessions)
            {
                var rows = fresh.Where(a => SessionScope.Matches(session, a)).OrderBy(a => a.Seq)
                    .Select(a => ActionRow.From(a, ddc)).ToList();
                if (rows.Count > 0)
                {
                    await ui.Clients.Group(UiHub.SessionGroup(session.Id)).SendAsync("ActionsAppended", session.Id, rows, ct);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live push failed for server {ServerId}", serverId);
        }
    }

    public async Task<BatchAck> IngestReadsAsync(AgentIdentity agent, ReadActionBatch batch, CancellationToken ct)
    {
        var reads = batch.Reads ?? [];
        if (reads.Count == 0)
        {
            return new BatchAck(batch.ServerId, 0, 0, 0);
        }

        if (reads.Count > MaxBatchItems)
        {
            throw new HubException($"Batch exceeds {MaxBatchItems} reads.");
        }

        if (batch.BatchId == Guid.Empty)
        {
            throw new HubException("ReadBatch requires a BatchId.");
        }

        await EnsureServerAsync(agent, batch.ServerId, ct);

        // Claim the batch id first; if the write below fails the claim is released so the resend is stored.
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wfsa.read_batches (batch_id, tenant_id, server_id)
            VALUES ({batch.BatchId}, {agent.TenantId}, {batch.ServerId})
            ON CONFLICT (batch_id) DO NOTHING
            """, ct);
        if (claimed == 0)
        {
            return new BatchAck(batch.ServerId, 0, 0, reads.Count);
        }

        try
        {
            await WriteReadsAsync(agent, batch, reads, ct);
        }
        catch
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wfsa.read_batches WHERE batch_id = {batch.BatchId}", CancellationToken.None);
            throw;
        }

        return new BatchAck(batch.ServerId, 0, reads.Count, 0);
    }

    private async Task WriteReadsAsync(AgentIdentity agent, ReadActionBatch batch, List<ReadAction> reads, CancellationToken ct)
    {
        var ddc = await LoadClassificationsAsync(agent.TenantId, batch.ServerId, reads.Select(r => r.PathHash), ct);
        var rows = reads.Select(r =>
        {
            ddc.TryGetValue(r.PathHash, out var c);
            var bucket = r.BucketStart.UtcDateTime;
            return new object[]
            {
                agent.TenantId, batch.ServerId,
                new DateTime(bucket.Year, bucket.Month, bucket.Day, bucket.Hour, 0, 0, DateTimeKind.Utc),
                r.ActorSid ?? "", unchecked((ulong)r.PathHash), r.FirstAt.UtcDateTime, r.LastAt.UtcDateTime, r.ReadCount,
                r.ObjectType.ToString(), r.Path ?? "", r.ActorName ?? "", r.ClientIp ?? "", r.ProcessName ?? "", r.Success,
                c?.ContentType ?? "", (sbyte)(c?.RiskLevel ?? -1), c?.ComplianceTags ?? [], c?.MonetaryValue ?? 0m,
            };
        });
        await writer.WriteAsync("wfsa.read_actions", ReadColumns, rows, ct);
    }

    // Updates the per-server driver health; the agent row itself is updated by the hub.
    public async Task RecordHeartbeatAsync(AgentIdentity agent, HeartbeatMessage heartbeat, CancellationToken ct)
    {
        var servers = heartbeat.Servers ?? [];
        if (servers.Count > 1000)
        {
            throw new HubException("Heartbeat lists too many servers.");
        }

        foreach (var s in servers)
        {
            await EnsureServerAsync(agent, s.ServerId, ct);
            var dropped = Math.Max(0, s.DroppedCount);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO wfsa.agent_positions (tenant_id, server_id, agent_id, last_seq, dropped_count, updated_at)
                VALUES ({agent.TenantId}, {s.ServerId}, {agent.AgentId}, 0, {dropped}, now())
                ON CONFLICT (server_id) DO UPDATE SET dropped_count = EXCLUDED.dropped_count, updated_at = now()
                """, ct);
        }
    }

    public async Task RecordIntegrityAsync(AgentIdentity agent, IntegrityEventMessage e, CancellationToken ct)
    {
        await EnsureServerAsync(agent, e.ServerId, ct);

        if (!Enum.IsDefined(e.Kind))
        {
            throw new HubException("Unknown integrity event kind.");
        }

        if (e.EndedAt is { } endedAt && endedAt < e.StartedAt)
        {
            throw new HubException("EndedAt is before StartedAt.");
        }

        var kind = e.Kind.ToString();
        var started = e.StartedAt.ToUniversalTime();
        var ended = e.EndedAt?.ToUniversalTime();
        var details = System.Text.Json.JsonSerializer.Serialize(new { droppedCount = e.DroppedCount, message = e.Details });

        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE wfsa.integrity_events SET
                ended_at = COALESCE({ended}, ended_at),
                first_seq = COALESCE({e.FirstSeq}, first_seq),
                last_seq = COALESCE({e.LastSeq}, last_seq),
                details = {details}::jsonb
            WHERE tenant_id = {agent.TenantId} AND server_id = {e.ServerId} AND kind = {kind} AND started_at = {started}
            """, ct);
        if (updated == 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO wfsa.integrity_events (tenant_id, server_id, kind, started_at, ended_at, first_seq, last_seq, details)
                VALUES ({agent.TenantId}, {e.ServerId}, {kind}, {started}, {ended}, {e.FirstSeq}, {e.LastSeq}, {details}::jsonb)
                """, ct);
        }

        if (e.Kind == IntegrityKind.DriverOverflow && e.DroppedCount is { } total)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO wfsa.agent_positions (tenant_id, server_id, agent_id, last_seq, dropped_count, updated_at)
                VALUES ({agent.TenantId}, {e.ServerId}, {agent.AgentId}, 0, {Math.Max(0, total)}, now())
                ON CONFLICT (server_id) DO UPDATE SET
                    dropped_count = GREATEST(wfsa.agent_positions.dropped_count, EXCLUDED.dropped_count), updated_at = now()
                """, ct);
        }
    }

    // An agent that is not connected cannot report, so its servers have a gap in coverage. The event is open until it reconnects.
    public async Task MarkAgentOfflineAsync(AgentIdentity agent, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO wfsa.integrity_events (tenant_id, server_id, kind, started_at)
                SELECT s.tenant_id, s.id, 'AgentOffline', now()
                FROM core.servers s
                WHERE s.agent_id = {agent.AgentId} AND s.tenant_id = {agent.TenantId} AND s.removed_at IS NULL
                  AND NOT EXISTS (SELECT 1 FROM wfsa.integrity_events e
                                  WHERE e.server_id = s.id AND e.kind = 'AgentOffline' AND e.ended_at IS NULL)
                """, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record AgentOffline for agent {AgentId}", agent.AgentId);
        }
    }

    public async Task MarkAgentOnlineAsync(AgentIdentity agent, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE wfsa.integrity_events e SET ended_at = now()
                FROM core.servers s
                WHERE e.server_id = s.id AND s.agent_id = {agent.AgentId} AND e.tenant_id = {agent.TenantId}
                  AND e.kind = 'AgentOffline' AND e.ended_at IS NULL
                """, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not close AgentOffline for agent {AgentId}", agent.AgentId);
        }
    }

    private async Task EnsureServerAsync(AgentIdentity agent, Guid serverId, CancellationToken ct)
    {
        var key = $"wfsa:server:{agent.AgentId}:{serverId}";
        if (cache.TryGetValue(key, out _))
        {
            return;
        }

        var owned = await db.Servers.AsNoTracking().AnyAsync(
            s => s.Id == serverId && s.TenantId == agent.TenantId && s.AgentId == agent.AgentId && s.RemovedAt == null, ct);
        if (!owned)
        {
            throw new HubException("Unknown server for this agent.");
        }

        cache.Set(key, true, TimeSpan.FromMinutes(5));
    }

    private async Task<Dictionary<long, FileClassificationRef>> LoadClassificationsAsync(
        Guid tenantId, Guid serverId, IEnumerable<long> pathHashes, CancellationToken ct)
    {
        var hashes = pathHashes.Distinct().ToList();
        return await db.FileClassifications.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ServerId == serverId && hashes.Contains(c.PathHash))
            .ToDictionaryAsync(c => c.PathHash, ct);
    }

    // The daily rollup is fed by an insert-time view, so a resent action must be skipped here
    // or it would be counted twice even though ReplacingMergeTree later collapses the row.
    private async Task<HashSet<ulong>> ExistingSeqsAsync(Guid tenantId, Guid serverId, List<AuditAction> actions, CancellationToken ct)
    {
        var minMs = actions.Min(a => a.OccurredAt.ToUnixTimeMilliseconds());
        var maxMs = actions.Max(a => a.OccurredAt.ToUnixTimeMilliseconds());
        var seqs = string.Join(',', actions.Select(a => (ulong)a.Seq));

        await using var connection = factory.Create();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT seq FROM wfsa.actions WHERE tenant_id = '{tenantId:D}' AND server_id = '{serverId:D}' " +
            $"AND occurred_at >= fromUnixTimestamp64Milli({minMs}, 'UTC') AND occurred_at <= fromUnixTimestamp64Milli({maxMs}, 'UTC') " +
            $"AND seq IN ({seqs})";

        var found = new HashSet<ulong>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add(Convert.ToUInt64(reader.GetValue(0)));
        }

        return found;
    }
}
