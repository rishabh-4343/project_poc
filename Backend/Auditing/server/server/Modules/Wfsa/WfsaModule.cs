using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql.NameTranslation;
using server.Common.Ch;
using server.Common.Db;
using server.Common.Modules;
using server.Modules.Wfsa.Data;
using server.Modules.Wfsa.Commands;
using server.Modules.Wfsa.Ingestion;
using server.Modules.Wfsa.Reports;
using server.Modules.Wfsa.Sessions;

namespace server.Modules.Wfsa;

// Slug, PostgreSQL schema and ClickHouse database are all "wfsa", isolating this module from the others.
public sealed class WfsaModule : IServerModule
{
    public const string PostgresSchema = "wfsa";
    public const string ClickHouseDatabase = "wfsa";

    public string Slug => "wfsa";

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' is required.");

        var names = new NpgsqlNullNameTranslator();

        services.AddDbContext<WfsaDbContext>(o => o.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MapEnum<Operation>("operation", PostgresSchema, names);
            npgsql.MapEnum<SessionState>("session_state", PostgresSchema, names);
        }));
        services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddMemoryCache();
        services.AddScoped<IngestionService>();
        services.AddScoped<CommandService>();
        services.AddSingleton<SessionNotifier>();
        services.AddHostedService<CommandExpiryWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wfsa");
        group.MapGet("/ping", () => Results.Ok(new { module = Slug, status = "ok" })).WithTags("Wfsa");
        group.MapSessionEndpoints();
        group.MapReportEndpoints();
    }

    public async Task MigrateAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<WfsaModule>>();
        var contentRoot = services.GetRequiredService<IHostEnvironment>().ContentRootPath;
        var postgres = services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Connection string 'Postgres' is required.");

        await PostgresModuleMigrator.ApplyAsync(postgres, contentRoot, PostgresSchema, logger);
        await ClickHouseSchemaMigrator.ApplyAsync(services.GetRequiredService<ClickHouseConnectionFactory>(), contentRoot, ClickHouseDatabase, logger);
    }
}
