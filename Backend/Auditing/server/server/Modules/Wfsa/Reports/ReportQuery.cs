using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;

namespace server.Modules.Wfsa.Reports;

// A predefined report over every captured action of the tenant (all servers, not one audit).
public sealed class ReportFilter
{
    public required Guid TenantId { get; init; }
    public required DateTimeOffset From { get; init; }
    public required IReadOnlyList<string> Operations { get; init; }
    public bool? Success { get; init; }
    public IReadOnlyList<string> ObjectTypes { get; init; } = [];
    public bool SharesOnly { get; init; }
    public bool LongPathsOnly { get; init; }
}

public static class ReportQuery
{
    // Windows' classic MAX_PATH; longer paths break many tools, which is why they get their own report.
    private const int LongPath = 260;

    // Newest first. The cursor is (time, seq, server) because seq is only unique within one server.
    public static ClickHouseCommand Build(
        ClickHouseConnection connection, ReportFilter f, (long Ms, ulong Seq, Guid Server)? before, int limit)
    {
        var command = connection.CreateCommand();
        var where = new List<string>
        {
            "tenant_id = {tenant:UUID}",
            "occurred_at >= fromUnixTimestamp64Milli({fromMs:Int64}, 'UTC')",
            "operation IN {ops:Array(String)}",
        };
        command.AddParameter("tenant", f.TenantId);
        command.AddParameter("fromMs", f.From.ToUnixTimeMilliseconds());
        command.AddParameter("ops", f.Operations.ToArray());

        if (f.Success is { } ok)
        {
            where.Add("success = {ok:Bool}");
            command.AddParameter("ok", ok);
        }

        if (f.ObjectTypes.Count > 0)
        {
            where.Add("object_type IN {types:Array(String)}");
            command.AddParameter("types", f.ObjectTypes.ToArray());
        }

        if (f.SharesOnly)
        {
            where.Add("share_name != ''");
        }

        if (f.LongPathsOnly)
        {
            where.Add($"(lengthUTF8(path) > {LongPath} OR lengthUTF8(new_path) > {LongPath})");
        }

        if (before is { } cursor)
        {
            where.Add("(occurred_at, seq, server_id) < (fromUnixTimestamp64Milli({curMs:Int64}, 'UTC'), {curSeq:UInt64}, {curServer:UUID})");
            command.AddParameter("curMs", cursor.Ms);
            command.AddParameter("curSeq", cursor.Seq);
            command.AddParameter("curServer", cursor.Server);
        }

        // FINAL so a not-yet-merged resend can never show twice.
        command.CommandText =
            $"SELECT {ActionQuery.Columns} FROM wfsa.actions FINAL WHERE {string.Join(" AND ", where)} " +
            $"ORDER BY occurred_at DESC, seq DESC, server_id DESC LIMIT {limit}";
        return command;
    }

    // Whether any action with these operations was ever stored for the tenant: false means "not being captured yet".
    public static ClickHouseCommand BuildCaptured(ClickHouseConnection connection, Guid tenantId, IReadOnlyList<string> operations)
    {
        var command = connection.CreateCommand();
        command.AddParameter("tenant", tenantId);
        command.AddParameter("ops", operations.ToArray());
        command.CommandText = "SELECT 1 FROM wfsa.actions WHERE tenant_id = {tenant:UUID} AND operation IN {ops:Array(String)} LIMIT 1";
        return command;
    }
}
