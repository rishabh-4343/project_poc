using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using server.Common.Auth;

namespace server.Common.Db;

public static class DevSeeder
{
    public const string DevAgentName = "dev-agent";

    public static async Task ApplyAsync(string postgresConnectionString, ILogger logger)
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        await connection.OpenAsync();

        await using (var cmd = new NpgsqlCommand(
            "insert into core.tenants (id, name, slug) values (@id, 'Dev Tenant', @slug) on conflict (id) do nothing;",
            connection))
        {
            cmd.Parameters.AddWithValue("id", DevIdentity.TenantId);
            cmd.Parameters.AddWithValue("slug", DevIdentity.TenantSlug);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = new NpgsqlCommand(
            "insert into core.users (id, tenant_id, email, display_name, role) values (@id, @tenant, @email, @name, @role) on conflict (id) do nothing;",
            connection))
        {
            cmd.Parameters.AddWithValue("id", DevIdentity.UserId);
            cmd.Parameters.AddWithValue("tenant", DevIdentity.TenantId);
            cmd.Parameters.AddWithValue("email", DevIdentity.Email);
            cmd.Parameters.AddWithValue("name", DevIdentity.DisplayName);
            cmd.Parameters.AddWithValue("role", DevIdentity.Role);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var check = new NpgsqlCommand(
            "select exists(select 1 from core.agents where tenant_id = @tenant and name = @name);", connection))
        {
            check.Parameters.AddWithValue("tenant", DevIdentity.TenantId);
            check.Parameters.AddWithValue("name", DevAgentName);
            if ((bool)(await check.ExecuteScalarAsync() ?? false))
            {
                logger.LogInformation("Dev seed already present. No new agent key issued.");
                return;
            }
        }

        var apiKey = "dev-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        await using (var insert = new NpgsqlCommand(
            "insert into core.agents (id, tenant_id, name, api_key_hash) values (@id, @tenant, @name, @hash);", connection))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("tenant", DevIdentity.TenantId);
            insert.Parameters.AddWithValue("name", DevAgentName);
            insert.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = AgentKeyHasher.Hash(apiKey) });
            await insert.ExecuteNonQueryAsync();
        }

        // Only the hash is stored, so this is the one time the key is visible.
        Console.WriteLine($"DEV AGENT KEY (shown once): {apiKey}");
        logger.LogInformation("Dev tenant, user and agent seeded.");
    }
}
