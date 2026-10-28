using ClickHouse.Client.Copy;

namespace server.Common.Ch;

public sealed class ClickHouseBulkWriter(ClickHouseConnectionFactory factory)
{
    public async Task WriteAsync(string table, IReadOnlyCollection<string> columns, IEnumerable<object[]> rows, CancellationToken ct)
    {
        await using var connection = factory.Create();
        await connection.OpenAsync(ct);

        using var bulk = new ClickHouseBulkCopy(connection)
        {
            DestinationTableName = table,
            ColumnNames = columns,
        };
        await bulk.InitAsync();
        await bulk.WriteToServerAsync(rows, ct);
    }
}
