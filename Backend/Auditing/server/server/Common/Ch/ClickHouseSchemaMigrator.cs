using System.Text.RegularExpressions;
using ClickHouse.Client.Utility;

namespace server.Common.Ch;

public static class ClickHouseSchemaMigrator
{
    public static async Task ApplyAsync(
        ClickHouseConnectionFactory factory, string contentRootPath, string database, ILogger logger)
    {
        var folder = ResolveFolder(contentRootPath, database);

        await using var connection = factory.Create();
        await connection.OpenAsync();

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT count() FROM system.tables WHERE database = {db:String}";
            check.AddParameter("db", database);
            if (Convert.ToInt64(await check.ExecuteScalarAsync()) > 0)
            {
                logger.LogInformation("ClickHouse database {Database} already has tables. Skipping initial scripts.", database);
                return;
            }
        }

        foreach (var file in Directory.GetFiles(folder, "*.sql").Order(StringComparer.Ordinal))
        {
            // ClickHouse runs one statement per command, so strip comments and split on ';'.
            var sql = Regex.Replace(await File.ReadAllTextAsync(file), @"--[^\r\n]*", "");
            foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync();
            }

            logger.LogInformation("Applied ClickHouse {Database} script {Script}", database, Path.GetFileName(file));
        }
    }

    private static string ResolveFolder(string contentRootPath, string database)
    {
        var current = new DirectoryInfo(contentRootPath);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "context", "auditor-pack", "schema", "ch", database);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException($"ClickHouse scripts not found under context/auditor-pack/schema/ch/{database}.");
    }
}
