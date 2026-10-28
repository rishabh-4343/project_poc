using Npgsql;

namespace server.Common.Db;

// Applies a module's ordered *.sql files, tracked in <schema>.schema_migrations (created by that module's 0001).
public static class PostgresModuleMigrator
{
    public static async Task ApplyAsync(
        string postgresConnectionString, string contentRootPath, string schema, ILogger logger)
    {
        var folder = ResolveFolder(contentRootPath, schema);

        await using var connection = new NpgsqlConnection(postgresConnectionString);
        await connection.OpenAsync();

        foreach (var file in Directory.GetFiles(folder, "*.sql").Order(StringComparer.Ordinal))
        {
            var version = Path.GetFileName(file);

            if (await IsAppliedAsync(connection, schema, version))
            {
                continue;
            }

            await using var tx = await connection.BeginTransactionAsync();

            await using (var run = new NpgsqlCommand(await File.ReadAllTextAsync(file), connection, tx))
            {
                await run.ExecuteNonQueryAsync();
            }

            await using (var track = new NpgsqlCommand(
                $"insert into {schema}.schema_migrations (version) values (@v) on conflict do nothing;", connection, tx))
            {
                track.Parameters.AddWithValue("v", version);
                await track.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
            logger.LogInformation("Applied {Schema} migration {Version}", schema, version);
        }
    }

    private static async Task<bool> IsAppliedAsync(NpgsqlConnection connection, string schema, string version)
    {
        await using var exists = new NpgsqlCommand("select to_regclass(@t) is not null;", connection);
        exists.Parameters.AddWithValue("t", $"{schema}.schema_migrations");
        if (!(bool)(await exists.ExecuteScalarAsync() ?? false))
        {
            return false;
        }

        await using var applied = new NpgsqlCommand(
            $"select exists(select 1 from {schema}.schema_migrations where version = @v);", connection);
        applied.Parameters.AddWithValue("v", version);
        return (bool)(await applied.ExecuteScalarAsync() ?? false);
    }

    private static string ResolveFolder(string contentRootPath, string schema)
    {
        var current = new DirectoryInfo(contentRootPath);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "context", "auditor-pack", "schema", "db", schema);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException($"Migration folder not found under context/auditor-pack/schema/db/{schema}.");
    }
}
