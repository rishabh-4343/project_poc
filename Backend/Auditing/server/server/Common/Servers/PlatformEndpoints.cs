using Microsoft.EntityFrameworkCore;
using server.Common.Auth;
using server.Common.Db;
using server.Common.Hubs;

namespace server.Common.Servers;

public sealed record RegisterServerRequest(Guid AgentId, string HostName, string? OsVersion);
public sealed record ServerDto(Guid Id, Guid AgentId, string HostName, string? OsVersion, bool Approved, DateTimeOffset CreatedAt);
public sealed record RegisterShareRequest(Guid ServerId, string Name, string LocalPath, string UncPath);
public sealed record ShareDto(Guid Id, Guid ServerId, string Name, string LocalPath, string UncPath, bool Approved);
public sealed record RegisterAgentRequest(string Name);
public sealed record RegisteredAgentDto(Guid Id, string Name, string ApiKey);
public sealed record AgentDto(
    Guid Id, string Name, bool Online, string Status, DateTimeOffset? LastSeenAt, string? AgentVersion, string? DriverVersion);

// core.servers / core.shares are platform-owned, so these live in Common rather than in a module.
public static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/agents", async (ICurrentTenant tenant, ConfigDbContext db, AgentConnectionTracker tracker, CancellationToken ct) =>
        {
            var agents = await db.Agents.AsNoTracking()
                .Where(a => a.TenantId == tenant.TenantId && a.RevokedAt == null)
                .OrderBy(a => a.Name)
                .ToListAsync(ct);

            return agents.Select(a => new AgentDto(
                a.Id, a.Name, tracker.IsOnline(a.Id), tracker.IsOnline(a.Id) ? "online" : "offline",
                a.LastSeenAt, a.AgentVersion, a.DriverVersion));
        });

        app.MapPost("/api/agents", async (RegisterAgentRequest req, ICurrentTenant tenant, ConfigDbContext db, CancellationToken ct) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (name.Length is 0 or > 100)
            {
                return Results.BadRequest(new { error = "name is required (1-100 characters)." });
            }

            if (await db.Agents.AnyAsync(a => a.TenantId == tenant.TenantId && a.Name == name && a.RevokedAt == null, ct))
            {
                return Results.Conflict(new { error = $"An agent named '{name}' already exists." });
            }

            // Only the hash is stored, so this response is the one time the key is visible.
            var apiKey = "ag-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var agent = new AgentRecord { Id = Guid.NewGuid(), TenantId = tenant.TenantId, Name = name, ApiKeyHash = AgentKeyHasher.Hash(apiKey) };
            db.Agents.Add(agent);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/agents/{agent.Id}", new RegisteredAgentDto(agent.Id, agent.Name, apiKey));
        })
        .WithTags("Agents")
        .Produces<RegisteredAgentDto>(StatusCodes.Status201Created)
        .Produces<ApiError>(StatusCodes.Status400BadRequest)
        .Produces<ApiError>(StatusCodes.Status409Conflict);

        // Replaces the key of an existing agent (same id, same servers). The old key stops working at once.
        app.MapPost("/api/agents/{id:guid}/rotate-key", async (Guid id, ICurrentTenant tenant, ConfigDbContext db, CancellationToken ct) =>
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenant.TenantId && a.RevokedAt == null, ct);
            if (agent is null)
            {
                return Results.NotFound();
            }

            var apiKey = "ag-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            agent.ApiKeyHash = AgentKeyHasher.Hash(apiKey);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new RegisteredAgentDto(agent.Id, agent.Name, apiKey));
        })
        .WithTags("Agents")
        .Produces<RegisteredAgentDto>()
        .Produces(StatusCodes.Status404NotFound);

        // A revoked key stops authenticating at once; a connection that is already open ends when it next reconnects.
        app.MapPost("/api/agents/{id:guid}/revoke", async (Guid id, ICurrentTenant tenant, ConfigDbContext db, CancellationToken ct) =>
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenant.TenantId && a.RevokedAt == null, ct);
            if (agent is null)
            {
                return Results.NotFound();
            }

            agent.RevokedAt = DateTimeOffset.UtcNow;
            agent.Status = "revoked";
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithTags("Agents")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        var servers = app.MapGroup("/api/servers").WithTags("Servers");

        servers.MapGet("/", async (ICurrentTenant tenant, ConfigDbContext db, CancellationToken ct) =>
            await db.Servers.AsNoTracking()
                .Where(s => s.TenantId == tenant.TenantId && s.RemovedAt == null)
                .OrderBy(s => s.HostName)
                .Select(s => new ServerDto(s.Id, s.AgentId, s.HostName, s.OsVersion, s.Approved, s.CreatedAt))
                .ToListAsync(ct));

        servers.MapPost("/", async (RegisterServerRequest req, ICurrentTenant tenant, ConfigDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.HostName))
            {
                return Results.BadRequest(new { error = "hostName is required." });
            }

            var host = req.HostName.Trim();

            if (!await db.Agents.AnyAsync(a => a.Id == req.AgentId && a.TenantId == tenant.TenantId, ct))
            {
                return Results.BadRequest(new { error = "agentId does not exist for this tenant." });
            }

            if (await db.Servers.AnyAsync(s => s.TenantId == tenant.TenantId && s.HostName == host, ct))
            {
                return Results.Conflict(new { error = $"Server '{host}' is already registered." });
            }

            var server = new ServerRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                AgentId = req.AgentId,
                HostName = host,
                OsVersion = req.OsVersion,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Servers.Add(server);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/servers/{server.Id}",
                new ServerDto(server.Id, server.AgentId, server.HostName, server.OsVersion, server.Approved, server.CreatedAt));
        })
        .Produces<ServerDto>(StatusCodes.Status201Created)
        .Produces<ApiError>(StatusCodes.Status400BadRequest)
        .Produces<ApiError>(StatusCodes.Status409Conflict);

        servers.MapPost("/{id:guid}/approve", async (Guid id, ICurrentTenant tenant, ConfigDbContext db, CancellationToken ct) =>
        {
            var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.TenantId && s.RemovedAt == null, ct);
            if (server is null)
            {
                return Results.NotFound();
            }

            server.Approved = true;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ServerDto(server.Id, server.AgentId, server.HostName, server.OsVersion, server.Approved, server.CreatedAt));
        })
        .Produces<ServerDto>()
        .Produces(StatusCodes.Status404NotFound);

        var shares = app.MapGroup("/api/shares").WithTags("Shares");

        shares.MapGet("/", async (Guid? serverId, ICurrentTenant tenant, ConfigDbContext db, CancellationToken ct) =>
            await db.Shares.AsNoTracking()
                .Where(s => s.TenantId == tenant.TenantId && s.RemovedAt == null && (serverId == null || s.ServerId == serverId))
                .OrderBy(s => s.Name)
                .Select(s => new ShareDto(s.Id, s.ServerId, s.Name, s.LocalPath, s.UncPath, s.Approved))
                .ToListAsync(ct));

        shares.MapPost("/", async (RegisterShareRequest req, ICurrentTenant tenant, ConfigDbContext db, ShareSync sync, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.LocalPath) || string.IsNullOrWhiteSpace(req.UncPath))
            {
                return Results.BadRequest(new { error = "name, localPath and uncPath are required." });
            }

            if (!await db.Servers.AnyAsync(s => s.Id == req.ServerId && s.TenantId == tenant.TenantId && s.RemovedAt == null, ct))
            {
                return Results.BadRequest(new { error = "serverId does not exist for this tenant." });
            }

            var localPath = req.LocalPath.Trim();
            if (await db.Shares.AnyAsync(s => s.ServerId == req.ServerId && s.LocalPath == localPath, ct))
            {
                return Results.Conflict(new { error = $"A share for '{localPath}' already exists on this server." });
            }

            var share = new ShareRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                ServerId = req.ServerId,
                Name = req.Name.Trim(),
                LocalPath = localPath,
                UncPath = req.UncPath.Trim(),
            };
            db.Shares.Add(share);
            await db.SaveChangesAsync(ct);
            await sync.PushForServerAsync(share.ServerId, ct);

            return Results.Created($"/api/shares/{share.Id}",
                new ShareDto(share.Id, share.ServerId, share.Name, share.LocalPath, share.UncPath, share.Approved));
        })
        .Produces<ShareDto>(StatusCodes.Status201Created)
        .Produces<ApiError>(StatusCodes.Status400BadRequest)
        .Produces<ApiError>(StatusCodes.Status409Conflict);
    }
}
