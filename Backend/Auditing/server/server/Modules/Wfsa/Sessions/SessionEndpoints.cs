using Microsoft.EntityFrameworkCore;
using server.Common;
using server.Common.Auth;
using server.Modules.Wfsa.Commands;
using server.Modules.Wfsa.Data;

namespace server.Modules.Wfsa.Sessions;

public sealed record FolderRequest(string LocalPath, Guid? ShareId, bool? Recursive);
public sealed record CreateSessionRequest(
    Guid ServerId, string? Name, List<Operation>? Operations, bool IncludeReads, List<FolderRequest>? Folders, DateTimeOffset? MaxEndAt);
public sealed record FolderDto(Guid? ShareId, string LocalPath, bool Recursive);
public sealed record SessionDto(
    Guid Id, Guid ServerId, Guid AgentId, string? Name, List<Operation> Operations, bool IncludeReads,
    SessionState State, Guid StartedBy, Guid? StoppedBy, DateTimeOffset RequestedAt, DateTimeOffset? ActiveAt,
    DateTimeOffset? StoppedAt, DateTimeOffset? MaxEndAt, string? FailureReason, List<FolderDto> Folders);

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder group)
    {
        var sessions = group.MapGroup("/sessions").WithTags("Sessions");

        sessions.MapPost("/", Create)
            .Produces<SessionDto>(StatusCodes.Status201Created)
            .Produces<ApiError>(StatusCodes.Status400BadRequest)
            .Produces<ApiError>(StatusCodes.Status409Conflict);
        sessions.MapGet("/", List)
            .Produces<List<SessionDto>>();
        sessions.MapGet("/{id:guid}", Get)
            .Produces<SessionDto>()
            .Produces(StatusCodes.Status404NotFound);
        sessions.MapPost("/{id:guid}/stop", Stop)
            .Produces<SessionDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ApiError>(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> Create(
        CreateSessionRequest req, ICurrentTenant tenant, ICurrentUser user, WfsaDbContext db, CommandService commands,
        SessionNotifier notifier, CancellationToken ct)
    {
        if (req.Operations is not { Count: > 0 })
        {
            return Results.BadRequest(new { error = "At least one operation is required." });
        }

        if (req.Folders is not { Count: > 0 } || req.Folders.Any(f => string.IsNullOrWhiteSpace(f.LocalPath)))
        {
            return Results.BadRequest(new { error = "At least one folder, each with a localPath, is required." });
        }

        var folders = req.Folders
            .Select(f => new SessionFolderRecord { ShareId = f.ShareId, LocalPath = f.LocalPath.Trim(), Recursive = f.Recursive ?? true })
            .ToList();

        if (folders.Select(f => f.LocalPath.ToLowerInvariant()).Distinct().Count() != folders.Count)
        {
            return Results.BadRequest(new { error = "Folder paths must be unique within a session." });
        }

        var server = await db.Servers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == req.ServerId && s.TenantId == tenant.TenantId && s.RemovedAt == null, ct);
        if (server is null)
        {
            return Results.BadRequest(new { error = "serverId does not exist for this tenant." });
        }

        if (!server.Approved)
        {
            return Results.Conflict(new { error = "Server is not approved." });
        }

        var shareIds = folders.Where(f => f.ShareId.HasValue).Select(f => f.ShareId!.Value).Distinct().ToList();
        if (shareIds.Count > 0)
        {
            var valid = await db.Shares.CountAsync(
                s => shareIds.Contains(s.Id) && s.TenantId == tenant.TenantId && s.ServerId == server.Id && s.RemovedAt == null, ct);
            if (valid != shareIds.Count)
            {
                return Results.BadRequest(new { error = "One or more shareIds do not belong to this server." });
            }
        }

        var session = new SessionRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            ServerId = server.Id,
            AgentId = server.AgentId,
            Name = req.Name?.Trim(),
            Operations = req.Operations.Distinct().ToList(),
            IncludeReads = req.IncludeReads,
            State = SessionState.Requested,
            StartedBy = user.UserId,
            RequestedAt = DateTimeOffset.UtcNow,
            MaxEndAt = req.MaxEndAt,
            Folders = folders,
        };
        db.Sessions.Add(session);
        await commands.IssueAsync(session, CommandTypes.StartScope, ct);
        await notifier.PublishAsync(session, ct);

        return Results.Created($"/api/wfsa/sessions/{session.Id}", ToDto(session));
    }

    private static async Task<IResult> List(
        SessionState? state, Guid? serverId, ICurrentTenant tenant, WfsaDbContext db, CancellationToken ct)
    {
        var rows = await db.Sessions.AsNoTracking().Include(s => s.Folders)
            .Where(s => s.TenantId == tenant.TenantId && (state == null || s.State == state) && (serverId == null || s.ServerId == serverId))
            .OrderByDescending(s => s.RequestedAt)
            .Take(200)
            .ToListAsync(ct);

        return Results.Ok(rows.Select(ToDto));
    }

    private static async Task<IResult> Get(Guid id, ICurrentTenant tenant, WfsaDbContext db, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().Include(s => s.Folders)
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.TenantId, ct);

        return session is null ? Results.NotFound() : Results.Ok(ToDto(session));
    }

    // A never-delivered session just cancels its pending start; a running one goes to Stopping and a
    // StopScope command, and the agent's result completes it.
    private static async Task<IResult> Stop(
        Guid id, ICurrentTenant tenant, ICurrentUser user, WfsaDbContext db, CommandService commands,
        SessionNotifier notifier, CancellationToken ct)
    {
        var session = await db.Sessions.Include(s => s.Folders)
            .FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenant.TenantId, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        session.StoppedBy = user.UserId;

        switch (session.State)
        {
            case SessionState.Requested:
                await commands.CancelPendingAsync(session.Id, ct);
                session.State = SessionState.Completed;
                session.StoppedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                break;
            case SessionState.Starting or SessionState.Active or SessionState.Degraded:
                session.State = SessionState.Stopping;
                await commands.IssueAsync(session, CommandTypes.StopScope, ct);
                break;
            default:
                return Results.Conflict(new { error = $"Session is already {session.State}." });
        }

        await notifier.PublishAsync(session, ct);
        return Results.Ok(ToDto(session));
    }

    private static SessionDto ToDto(SessionRecord s) => new(
        s.Id, s.ServerId, s.AgentId, s.Name, s.Operations, s.IncludeReads, s.State, s.StartedBy, s.StoppedBy,
        s.RequestedAt, s.ActiveAt, s.StoppedAt, s.MaxEndAt, s.FailureReason,
        s.Folders.Select(f => new FolderDto(f.ShareId, f.LocalPath, f.Recursive)).ToList());
}
