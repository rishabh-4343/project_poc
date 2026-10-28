using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using server.Common.Auth;
using server.Common.Ch;
using server.Common.Db;
using server.Common.Hubs;
using server.Common.Modules;
using server.Common.Servers;
using server.Modules.Wfsa;

var builder = WebApplication.CreateBuilder(args);

var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
	?? throw new InvalidOperationException("Connection string 'Postgres' is required.");

var clickHouseConnectionString = builder.Configuration.GetConnectionString("ClickHouse")
	?? throw new InvalidOperationException("Connection string 'ClickHouse' is required.");

builder.Services.AddDbContext<ConfigDbContext>(options =>
	options.UseNpgsql(postgresConnectionString));
builder.Services.AddSingleton(new ClickHouseConnectionFactory(clickHouseConnectionString));
builder.Services.AddSingleton<ClickHouseBulkWriter>();
builder.Services.AddSingleton<AgentConnectionTracker>();
builder.Services.AddScoped<ShareSync>();

builder.Services.AddScoped<ICurrentTenant, DevCurrentTenant>();
builder.Services.AddScoped<ICurrentUser, DevCurrentUser>();

builder.Services.AddAuthentication()
	.AddScheme<AuthenticationSchemeOptions, AgentKeyAuthHandler>(AgentKeyAuthHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
	.AddPolicy("Agent", p => p.AddAuthenticationSchemes(AgentKeyAuthHandler.SchemeName).RequireAuthenticatedUser());

builder.Services.AddSignalR()
	.AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
	.AddHubOptions<AgentHub>(o => o.MaximumReceiveMessageSize = 8 * 1024 * 1024);

builder.AddModule<WfsaModule>();

builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
	document.Info = new() { Title = "Auditor API", Version = "v1" };
	return Task.CompletedTask;
}));

// Browser origins allowed to call the API and hubs; set Cors:AllowedOrigins per environment.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy("Frontend", p => p
	.WithOrigins(allowedOrigins)
	.AllowAnyHeader()
	.AllowAnyMethod()
	.AllowCredentials()
	.WithExposedHeaders("Content-Disposition")));

var app = builder.Build();

await CoreSchemaMigrator.ApplyAsync(postgresConnectionString, app.Environment.ContentRootPath, app.Logger);
await DevSeeder.ApplyAsync(postgresConnectionString, app.Logger);
await app.MigrateModulesAsync();

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok()).WithTags("System");

app.MapHub<UiHub>("/hubs/ui");
app.MapHub<AgentHub>("/hubs/agent");

app.MapPlatformEndpoints();
app.MapModules();

// Contract and UI are for development; a production host does not publish them.
if (app.Environment.IsDevelopment())
{
	app.MapOpenApi();
	app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Auditor API v1"));
}

app.Run();
