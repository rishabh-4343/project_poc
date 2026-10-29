using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;

namespace server.Modules.Wfsa.Reports;

// One user's content views of one path within one hour (the agent's merge unit), summed across batches.
public sealed record ContentViewRow(
    Guid ServerId, DateTimeOffset BucketStart, DateTimeOffset FirstAt, DateTimeOffset LastAt, ulong ReadCount, string ObjectType,
    string Path, string ActorSid, string? ActorName, string? ClientIp, string? ProcessName, bool Success, string ContentType,
    int RiskLevel);

// Content views plus whether any read was ever stored for the tenant (false means "not being captured yet").
public sealed record ContentViewReport(List<ContentViewRow> Items, bool Captured);

// Rows of wfsa.read_actions: for one audit (its server, folders and time window, same scope rule as ActionQuery),
// or for the whole tenant (the read reports).
public static class ContentViewQuery
{
    public static ClickHouseCommand Build(ClickHouseConnection connection, ActionFilter f, int limit)
    {
        var command = connection.CreateCommand();
        var where = new List<string>
        {
            "tenant_id = {tenant:UUID}",
            "server_id = {server:UUID}",
            "last_at >= fromUnixTimestamp64Milli({fromMs:Int64}, 'UTC')",
        };
        command.AddParameter("tenant", f.TenantId);
        command.AddParameter("server", f.ServerId);
        command.AddParameter("fromMs", f.From.ToUnixTimeMilliseconds());
        command.AddParameter("sep", "\\");

        if (f.To is { } to)
        {
            where.Add("first_at <= fromUnixTimestamp64Milli({toMs:Int64}, 'UTC')");
            command.AddParameter("toMs", to.ToUnixTimeMilliseconds());
        }

        var scope = f.Folders.Select((folder, i) => ActionQuery.PrefixClause(command, $"f{i}", folder.LocalPath, folder.Recursive)).ToList();
        where.Add($"({string.Join(" OR ", scope)})");

        command.CommandText = Select(where, limit);
        return command;
    }

    public static ClickHouseCommand BuildForTenant(ClickHouseConnection connection, Guid tenantId, DateTimeOffset from, bool? success, int limit)
    {
        var command = connection.CreateCommand();
        var where = new List<string> { "tenant_id = {tenant:UUID}", "last_at >= fromUnixTimestamp64Milli({fromMs:Int64}, 'UTC')" };
        command.AddParameter("tenant", tenantId);
        command.AddParameter("fromMs", from.ToUnixTimeMilliseconds());

        if (success is { } ok)
        {
            where.Add("success = {ok:Bool}");
            command.AddParameter("ok", ok);
        }

        command.CommandText = Select(where, limit);
        return command;
    }

    public static ClickHouseCommand BuildCaptured(ClickHouseConnection connection, Guid tenantId)
    {
        var command = connection.CreateCommand();
        command.AddParameter("tenant", tenantId);
        command.CommandText = "SELECT 1 FROM wfsa.read_actions WHERE tenant_id = {tenant:UUID} LIMIT 1";
        return command;
    }

    // Grouped on the table key (plus success) so rows the SummingMergeTree has not merged yet still come back as one.
    // No aliases: ClickHouse would let an alias such as "path" shadow the column inside WHERE.
    private static string Select(List<string> where, int limit) =>
        "SELECT server_id, toUnixTimestamp(bucket_start), toUnixTimestamp64Milli(min(first_at)), toUnixTimestamp64Milli(max(last_at)), " +
        "sum(read_count), any(object_type), any(path), actor_sid, any(actor_name), any(client_ip), any(process_name), " +
        "success, any(content_type), any(risk_level) " +
        $"FROM wfsa.read_actions WHERE {string.Join(" AND ", where)} " +
        $"GROUP BY server_id, bucket_start, actor_sid, path_hash, success ORDER BY max(last_at) DESC LIMIT {limit}";

    public static ContentViewRow ReadRow(System.Data.Common.DbDataReader r) =>
        new(
            (Guid)r.GetValue(0),
            DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(r.GetValue(1))),
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(r.GetValue(2))),
            DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(r.GetValue(3))),
            Convert.ToUInt64(r.GetValue(4)), r.GetString(5), r.GetString(6), r.GetString(7),
            ActionRow.NullIfEmpty(r.GetString(8)), ActionRow.NullIfEmpty(r.GetString(9)), ActionRow.NullIfEmpty(r.GetString(10)),
            Convert.ToBoolean(r.GetValue(11)), r.GetString(12), Convert.ToInt32(r.GetValue(13)));
}
