using Npgsql;

namespace server.Common.Db;

public static class CoreSchemaMigrator
{
    public static async Task ApplyAsync(string postgresConnectionString, string contentRootPath, ILogger logger)
    {
        var scriptsPath = ResolveCoreScriptsPath(contentRootPath);

        var scriptFilePath = Path.Combine(scriptsPath, "0001_core.sql");
        if (!File.Exists(scriptFilePath))
        {
            throw new FileNotFoundException("Core schema script not found.", scriptFilePath);
        }

        await using var connection = new NpgsqlConnection(postgresConnectionString);
        await connection.OpenAsync();

        await using (var checkCommand = new NpgsqlCommand(
            "select exists(select 1 from information_schema.schemata where schema_name = 'core');",
            connection))
        {
            var exists = (bool)(await checkCommand.ExecuteScalarAsync() ?? false);
            if (exists)
            {
                logger.LogInformation("Core schema already exists. Skipping initial core script.");
                return;
            }
        }

        var sql = await File.ReadAllTextAsync(scriptFilePath);

        await using var applyCommand = new NpgsqlCommand(sql, connection);
        await applyCommand.ExecuteNonQueryAsync();

        logger.LogInformation("Core schema initialized from {ScriptPath}", scriptFilePath);
    }

    private static string ResolveCoreScriptsPath(string contentRootPath)
    {
        var current = new DirectoryInfo(contentRootPath);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "context", "auditor-pack", "schema", "db", "core");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Core schema scripts path not found under context/auditor-pack/schema/db/core.");
    }
}
