using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using server.Common.Db;
using server.Common.Hubs;

namespace server.Common.Servers;

public sealed record ShareInfo(string Name, string LocalPath);

// Sent to the agent as the client method UpdateShares: the full share list of one of its servers.
public sealed record UpdateSharesMessage(Guid ServerId, List<ShareInfo> Shares);

// Tells agents about their servers' shares, so they can name the share an action happened in.
public sealed class ShareSync(ConfigDbContext db, IHubContext<AgentHub> hub, AgentConnectionTracker tracker, ILogger<ShareSync> logger)
{
    // On connect: one message per server the agent serves.
    public async Task PushForAgentAsync(Guid agentId, CancellationToken ct)
    {
        var serverIds = await db.Servers.AsNoTracking()
            .Where(s => s.AgentId == agentId && s.RemovedAt == null)
            .Select(s => s.Id).ToListAsync(ct);

        foreach (var serverId in serverIds)
        {
            await SendAsync(agentId, serverId, ct);
        }
    }

    // After a share changes: only if its agent is connected (otherwise the next connect sends it).
    public async Task PushForServerAsync(Guid serverId, CancellationToken ct)
    {
        var agentId = await db.Servers.AsNoTracking().Where(s => s.Id == serverId).Select(s => (Guid?)s.AgentId).FirstOrDefaultAsync(ct);
        if (agentId is { } id && tracker.IsOnline(id))
        {
            await SendAsync(id, serverId, ct);
        }
    }

    private async Task SendAsync(Guid agentId, Guid serverId, CancellationToken ct)
    {
        try
        {
            var shares = await db.Shares.AsNoTracking()
                .Where(s => s.ServerId == serverId && s.RemovedAt == null)
                .OrderBy(s => s.Name)
                .Select(s => new ShareInfo(s.Name, s.LocalPath))
                .ToListAsync(ct);

            await hub.Clients.Group(AgentHub.GroupFor(agentId)).SendAsync("UpdateShares", new UpdateSharesMessage(serverId, shares), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not push shares for server {ServerId}", serverId);
        }
    }
}
