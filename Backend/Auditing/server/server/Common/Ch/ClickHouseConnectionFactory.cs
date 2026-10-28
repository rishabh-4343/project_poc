using ClickHouse.Client.ADO;

namespace server.Common.Ch;

public sealed class ClickHouseConnectionFactory(string connectionString)
{
    public ClickHouseConnection Create() => new(connectionString);
}
