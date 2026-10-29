using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using server.Modules.Wfsa.Commands;
using server.Modules.Wfsa.Data;
using server.Modules.Wfsa.Ingestion;

namespace server.Modules.Wfsa.Reports;

// The slice of wfsa.actions a session (or an extra user filter) selects. All values go in as query parameters.
public sealed class ActionFilter
{
    public required Guid TenantId { get; init; }
    public required Guid ServerId { get; init; }
    public required DateTimeOffset From { get; init; }
    public DateTimeOffset? To { get; init; }
    public required IReadOnlyList<string> Operations { get; init; }
    public required IReadOnlyList<ScopeFolder> Folders { get; init; }
    public string? Actor { get; init; }
    public bool? Success { get; init; }
    public string? PathPrefix { get; init; }
}

public static class ActionQuery
{
    internal const string Columns =
        "server_id, seq, toUnixTimestamp64Milli(occurred_at), object_type, operation, success, certainty, path, new_path, " +
        "share_name, actor_sid, actor_name, client_ip, process_name, old_security, new_security, content_type, risk_level, " +
        "compliance_tags, toString(monetary_value), details";

    public static ClickHouseCommand Build(
        ClickHouseConnection connection, ActionFilter f, (long Ms, ulong Seq)? before, bool descending, int? limit)
    {
        var command = connection.CreateCommand();
        var where = new List<string>
        {
            "tenant_id = {tenant:UUID}",
            "server_id = {server:UUID}",
            "occurred_at >= fromUnixTimestamp64Milli({fromMs:Int64}, 'UTC')",
            "operation IN {ops:Array(String)}",
        };
        command.AddParameter("tenant", f.TenantId);
        command.AddParameter("server", f.ServerId);
        command.AddParameter("fromMs", f.From.ToUnixTimeMilliseconds());
        command.AddParameter("ops", f.Operations.ToArray());
        command.AddParameter("sep", "\\");

        if (f.To is { } to)
        {
            where.Add("occurred_at <= fromUnixTimestamp64Milli({toMs:Int64}, 'UTC')");
            command.AddParameter("toMs", to.ToUnixTimeMilliseconds());
        }

        var scope = f.Folders.Select((folder, i) => PrefixClause(command, $"f{i}", folder.LocalPath, folder.Recursive)).ToList();
        where.Add($"({string.Join(" OR ", scope)})");

        if (!string.IsNullOrWhiteSpace(f.PathPrefix))
        {
            where.Add(PrefixClause(command, "x", f.PathPrefix, recursive: true));
        }

        if (!string.IsNullOrWhiteSpace(f.Actor))
        {
            where.Add("(positionCaseInsensitiveUTF8(actor_name, {actor:String}) > 0 OR actor_sid = {actor:String})");
            command.AddParameter("actor", f.Actor.Trim());
        }

        if (f.Success is { } ok)
        {
            where.Add("success = {ok:Bool}");
            command.AddParameter("ok", ok);
        }

        if (before is { } cursor)
        {
            where.Add(
                "(occurred_at < fromUnixTimestamp64Milli({curMs:Int64}, 'UTC') OR " +
                "(occurred_at = fromUnixTimestamp64Milli({curMs:Int64}, 'UTC') AND seq < {curSeq:UInt64}))");
            command.AddParameter("curMs", cursor.Ms);
            command.AddParameter("curSeq", cursor.Seq);
        }

        var direction = descending ? "DESC" : "ASC";
        // FINAL so a not-yet-merged resend can never show twice.
        command.CommandText =
            $"SELECT {Columns} FROM wfsa.actions FINAL WHERE {string.Join(" AND ", where)} " +
            $"ORDER BY occurred_at {direction}, seq {direction}" + (limit is { } n ? $" LIMIT {n}" : "");
        return command;
    }

    internal static string PrefixClause(ClickHouseCommand command, string name, string path, bool recursive)
    {
        var trimmed = path.Trim().TrimEnd('\\');
        command.AddParameter($"p{name}", trimmed);
        command.AddParameter($"s{name}", trimmed + "\\");

        var under = $"startsWith(lowerUTF8(path), lowerUTF8({{s{name}:String}}))";
        var inside = recursive
            ? under
            : $"({under} AND positionUTF8(substringUTF8(path, lengthUTF8({{s{name}:String}}) + 1), {{sep:String}}) = 0)";

        return $"(lowerUTF8(path) = lowerUTF8({{p{name}:String}}) OR {inside})";
    }

    public static ActionRow ReadRow(System.Data.Common.DbDataReader r)
    {
        var tags = r.GetValue(18) is Array a ? a.Cast<object>().Select(x => x?.ToString() ?? "").ToArray() : [];
        return new ActionRow(
            (Guid)r.GetValue(0),
            Convert.ToInt64(r.GetValue(1)),
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(r.GetValue(2))),
            r.GetString(3), r.GetString(4), Convert.ToBoolean(r.GetValue(5)), r.GetString(6),
            r.GetString(7), ActionRow.NullIfEmpty(r.GetString(8)), ActionRow.NullIfEmpty(r.GetString(9)),
            r.GetString(10), ActionRow.NullIfEmpty(r.GetString(11)), ActionRow.NullIfEmpty(r.GetString(12)),
            ActionRow.NullIfEmpty(r.GetString(13)), ActionRow.NullIfEmpty(r.GetString(14)), ActionRow.NullIfEmpty(r.GetString(15)),
            r.GetString(16), Convert.ToInt32(r.GetValue(17)), tags,
            decimal.Parse(r.GetString(19), System.Globalization.CultureInfo.InvariantCulture),
            ActionRow.NullIfEmpty(r.GetString(20)));
    }
}

// Same scope rule as the SQL above, applied in memory to decide which live sessions an ingested action belongs to.
public static class SessionScope
{
    public static DateTimeOffset Start(SessionRecord session) => session.ActiveAt ?? session.RequestedAt;

    public static bool Matches(SessionRecord session, AuditAction a)
    {
        if (a.OccurredAt < Start(session) || (session.StoppedAt is { } end && a.OccurredAt > end)
            || !session.Operations.Contains(a.Operation))
        {
            return false;
        }

        var path = a.Path ?? "";
        foreach (var folder in session.Folders)
        {
            var root = folder.LocalPath.Trim().TrimEnd('\\');
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
                && (folder.Recursive || path.IndexOf('\\', root.Length + 1) < 0))
            {
                return true;
            }
        }

        return false;
    }
}
