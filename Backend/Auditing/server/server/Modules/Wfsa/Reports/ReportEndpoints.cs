using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using server.Common;
using server.Common.Auth;
using server.Common.Ch;
using server.Modules.Wfsa.Commands;
using server.Modules.Wfsa.Data;

namespace server.Modules.Wfsa.Reports;

public sealed record ActionPage(List<ActionRow> Items, string? NextCursor);
public sealed record DayCount(string Day, ulong Actions, ulong Denied);
public sealed record OperationCount(string Operation, ulong Actions);
public sealed record ActorCount(string ActorSid, ulong Actions);
public sealed record Dashboard(
    int Days, ulong EventsToday, ulong DeniedToday, int ActiveSessions,
    List<DayCount> PerDay, List<OperationCount> ByOperation, List<ActorCount> TopActors);

public sealed record IntegrityEventDto(
    long Id, Guid ServerId, string Kind, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, long? FirstSeq, long? LastSeq, string? Details);

public static class ReportEndpoints
{
    private const int MaxPage = 200;

    public static void MapReportEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/sessions/{id:guid}/actions", GetActions)
            .WithTags("Reports")
            .Produces<ActionPage>()
            .Produces<ApiError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{id:guid}/export.csv", ExportCsv)
            .WithTags("Reports")
            .Produces<string>(StatusCodes.Status200OK, contentType: "text/csv")
            .Produces<ApiError>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        group.MapGet("/sessions/{id:guid}/integrity-events", GetSessionIntegrity)
            .WithTags("Reports")
            .Produces<List<IntegrityEventDto>>()
            .Produces(StatusCodes.Status404NotFound);
        group.MapGet("/integrity-events", GetRecentIntegrity)
            .WithTags("Reports")
            .Produces<List<IntegrityEventDto>>();
        group.MapGet("/dashboard", GetDashboard)
            .WithTags("Reports")
            .Produces<Dashboard>();
    }

    private static async Task<IResult> GetActions(
        Guid id, string? before, int? limit, string[]? operation, string? actor, bool? success, string? pathPrefix,
        ICurrentTenant tenant, WfsaDbContext db, ClickHouseConnectionFactory factory, CancellationToken ct)
    {
        (long, ulong)? cursor = null;
        if (!string.IsNullOrEmpty(before))
        {
            var parts = before.Split('-');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ms) || !ulong.TryParse(parts[1], out var seq))
            {
                return Results.BadRequest(new { error = "before must look like '<ms>-<seq>'." });
            }

            cursor = (ms, seq);
        }

        var ops = ParseOperations(operation, out var badOperation);
        if (badOperation is not null)
        {
            return Results.BadRequest(new { error = $"Unknown operation '{badOperation}'." });
        }

        var (session, serverName) = await LoadAsync(id, tenant, db, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        var pageSize = Math.Clamp(limit ?? MaxPage, 1, MaxPage);
        var filter = BuildFilter(session, ops, actor, success, pathPrefix);

        await using var connection = factory.Create();
        await connection.OpenAsync(ct);
        await using var command = ActionQuery.Build(connection, filter, cursor, descending: true, limit: pageSize + 1);

        var rows = new List<ActionRow>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add(ActionQuery.ReadRow(reader));
            }
        }

        string? next = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            next = $"{last.OccurredAt.ToUnixTimeMilliseconds()}-{last.Seq}";
        }

        return Results.Ok(new ActionPage(rows, next));
    }

    private static async Task<IResult> ExportCsv(
        Guid id, string[]? operation, string? actor, bool? success, string? pathPrefix,
        ICurrentTenant tenant, WfsaDbContext db, ClickHouseConnectionFactory factory, CancellationToken ct)
    {
        var ops = ParseOperations(operation, out var badOperation);
        if (badOperation is not null)
        {
            return Results.BadRequest(new { error = $"Unknown operation '{badOperation}'." });
        }

        var (session, serverName) = await LoadAsync(id, tenant, db, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        var filter = BuildFilter(session, ops, actor, success, pathPrefix);
        var fileName = $"session-{session.Id:N}.csv";

        return Results.Stream(async stream =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
            await writer.WriteLineAsync(
                "When (UTC),Server,Who,SID,Object,Path,New path,Operation,Allowed,Certainty,Process,From,Content type,Risk,Tags,Value");

            await using var connection = factory.Create();
            await connection.OpenAsync(ct);
            await using var command = ActionQuery.Build(connection, filter, before: null, descending: false, limit: null);
            await using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var r = ActionQuery.ReadRow(reader);
                await writer.WriteLineAsync(string.Join(',',
                    Csv(r.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                    Csv(serverName), Csv(r.ActorName), Csv(r.ActorSid), Csv(r.ObjectType), Csv(r.Path), Csv(r.NewPath),
                    Csv(r.Operation), r.Success ? "Allowed" : "Denied", Csv(r.Certainty), Csv(r.ProcessName), Csv(r.ClientIp),
                    Csv(r.ContentType), r.RiskLevel.ToString(CultureInfo.InvariantCulture), Csv(string.Join(';', r.ComplianceTags)),
                    r.MonetaryValue.ToString(CultureInfo.InvariantCulture)));
            }

            await writer.FlushAsync(ct);
        }, "text/csv", fileName);
    }

    private static async Task<IResult> GetDashboard(
        int? days, ICurrentTenant tenant, WfsaDbContext db, ClickHouseConnectionFactory factory, CancellationToken ct)
    {
        var span = Math.Clamp(days ?? 7, 1, 90);

        var active = await db.Sessions.CountAsync(
            s => s.TenantId == tenant.TenantId && (s.State == SessionState.Active || s.State == SessionState.Degraded), ct);

        await using var connection = factory.Create();
        await connection.OpenAsync(ct);

        async Task<List<T>> Query<T>(string sql, Func<System.Data.Common.DbDataReader, T> map)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql.Replace("{t}", $"'{tenant.TenantId:D}'").Replace("{d}", span.ToString(CultureInfo.InvariantCulture));
            var list = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(map(reader));
            }

            return list;
        }

        const string window = "FROM wfsa.daily_action_counts WHERE tenant_id = {t} AND day > today() - {d}";

        var perDay = await Query(
            $"SELECT toString(day), sum(actions), sumIf(actions, success = 0) {window} GROUP BY day ORDER BY day",
            r => new DayCount(r.GetString(0), Convert.ToUInt64(r.GetValue(1)), Convert.ToUInt64(r.GetValue(2))));
        var byOperation = await Query(
            $"SELECT operation, sum(actions) AS n {window} GROUP BY operation ORDER BY n DESC",
            r => new OperationCount(r.GetString(0), Convert.ToUInt64(r.GetValue(1))));
        var topActors = await Query(
            $"SELECT actor_sid, sum(actions) AS n {window} GROUP BY actor_sid ORDER BY n DESC LIMIT 10",
            r => new ActorCount(r.GetString(0), Convert.ToUInt64(r.GetValue(1))));

        var today = perDay.FirstOrDefault(d => d.Day == DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        return Results.Ok(new Dashboard(span, today?.Actions ?? 0, today?.Denied ?? 0, active, perDay, byOperation, topActors));
    }

    // Events on the session's server that overlap its time window, so a report shows the gaps that affect it.
    private static async Task<IResult> GetSessionIntegrity(Guid id, ICurrentTenant tenant, WfsaDbContext db, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.TenantId, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        var from = SessionScope.Start(session);
        var to = session.StoppedAt;
        var events = await db.IntegrityEvents.AsNoTracking()
            .Where(e => e.TenantId == tenant.TenantId && e.ServerId == session.ServerId
                && (to == null || e.StartedAt <= to) && (e.EndedAt == null || e.EndedAt >= from))
            .OrderByDescending(e => e.StartedAt).Take(200)
            .ToListAsync(ct);

        return Results.Ok(events.Select(ToDto).ToList());
    }

    private static async Task<IResult> GetRecentIntegrity(int? limit, ICurrentTenant tenant, WfsaDbContext db, CancellationToken ct)
    {
        var events = await db.IntegrityEvents.AsNoTracking()
            .Where(e => e.TenantId == tenant.TenantId)
            .OrderByDescending(e => e.StartedAt).Take(Math.Clamp(limit ?? 20, 1, 100))
            .ToListAsync(ct);

        return Results.Ok(events.Select(ToDto).ToList());
    }

    private static IntegrityEventDto ToDto(IntegrityEventRecord e) =>
        new(e.Id, e.ServerId, e.Kind, e.StartedAt, e.EndedAt, e.FirstSeq, e.LastSeq, e.Details);

    private static async Task<(SessionRecord? Session, string ServerName)> LoadAsync(
        Guid id, ICurrentTenant tenant, WfsaDbContext db, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().Include(s => s.Folders)
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.TenantId, ct);
        if (session is null)
        {
            return (null, "");
        }

        var name = await db.Servers.AsNoTracking().Where(s => s.Id == session.ServerId).Select(s => s.HostName).FirstOrDefaultAsync(ct);
        return (session, name ?? session.ServerId.ToString());
    }

    // A caller may narrow a session's operations but never widen them.
    private static ActionFilter BuildFilter(SessionRecord s, List<Operation>? requested, string? actor, bool? success, string? pathPrefix)
    {
        var ops = requested is { Count: > 0 } ? s.Operations.Intersect(requested) : s.Operations;

        return new ActionFilter
        {
            TenantId = s.TenantId,
            ServerId = s.ServerId,
            From = SessionScope.Start(s),
            To = s.StoppedAt,
            Operations = ops.Select(o => o.ToString()).ToList(),
            Folders = s.Folders.Select(f => new ScopeFolder(f.LocalPath, f.Recursive, f.ShareId)).ToList(),
            Actor = actor,
            Success = success,
            PathPrefix = pathPrefix,
        };
    }

    private static List<Operation>? ParseOperations(string[]? values, out string? bad)
    {
        bad = null;
        if (values is not { Length: > 0 })
        {
            return null;
        }

        var result = new List<Operation>();
        foreach (var name in values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!Enum.TryParse<Operation>(name, ignoreCase: true, out var op) || !Enum.IsDefined(op))
            {
                bad = name;
                return null;
            }

            result.Add(op);
        }

        return result;
    }

    // Leading = + - @ would be run as a formula by spreadsheet apps, and paths/names here come from file servers.
    private static string Csv(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
