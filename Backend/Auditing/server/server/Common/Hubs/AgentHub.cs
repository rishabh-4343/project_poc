using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using server.Common.Auth;
using server.Common.Db;
using server.Common.Servers;
using server.Modules.Wfsa.Commands;
using server.Modules.Wfsa.Ingestion;

namespace server.Common.Hubs;

[Authorize(Policy = "Agent")]
public sealed class AgentHub(IngestionService ingest, CommandService commands, AgentConnectionTracker tracker, ConfigDbContext config, IHubContext<UiHub> ui, ShareSync shares) : Hub
{
    public static string GroupFor(Guid agentId) => $"agent:{agentId}";

    public override async Task OnConnectedAsync()
    {
        var agent = Context.User!.Agent();
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(agent.AgentId));
        tracker.Connected(agent.AgentId);
        await MarkSeenAsync(agent.AgentId, "online");
        await commands.ResendPendingAsync(agent, Context.ConnectionAborted);
        await commands.ResendActiveScopesAsync(agent, Context.ConnectionAborted);
        await shares.PushForAgentAsync(agent.AgentId, Context.ConnectionAborted);
        await ingest.MarkAgentOnlineAsync(agent, Context.ConnectionAborted);
        await PublishAgentAsync(agent);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var agent = Context.User!.Agent();
        var agentId = agent.AgentId;
        tracker.Disconnected(agentId);
        if (!tracker.IsOnline(agentId))
        {
            await MarkSeenAsync(agentId, "offline");
            await ingest.MarkAgentOfflineAsync(agent, CancellationToken.None);
            await PublishAgentAsync(agent);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private Task MarkSeenAsync(Guid agentId, string status) =>
        config.Agents.Where(a => a.Id == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, status).SetProperty(a => a.LastSeenAt, DateTimeOffset.UtcNow));

    public Task CommandResult(CommandResultMessage result) =>
        commands.CompleteAsync(Context.User!.Agent(), result, Context.ConnectionAborted);

    public Task<BatchAck> EventBatch(ActionBatch batch) =>
        ingest.IngestActionsAsync(Context.User!.Agent(), batch, Context.ConnectionAborted);

    public async Task Heartbeat(HeartbeatMessage heartbeat)
    {
        var agent = Context.User!.Agent();
        await config.Agents.Where(a => a.Id == agent.AgentId).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.AgentVersion, heartbeat.AgentVersion)
            .SetProperty(a => a.DriverVersion, heartbeat.DriverVersion)
            .SetProperty(a => a.Status, "online")
            .SetProperty(a => a.LastSeenAt, DateTimeOffset.UtcNow), Context.ConnectionAborted);
        await ingest.RecordHeartbeatAsync(agent, heartbeat, Context.ConnectionAborted);
        await PublishAgentAsync(agent);
    }

    public Task IntegrityEvent(IntegrityEventMessage message) =>
        ingest.RecordIntegrityAsync(Context.User!.Agent(), message, Context.ConnectionAborted);

    // The tenant group merges this by id; a failed push never fails the call.
    private async Task PublishAgentAsync(AgentIdentity agent)
    {
        try
        {
            var a = await config.Agents.AsNoTracking().FirstAsync(x => x.Id == agent.AgentId);
            var online = tracker.IsOnline(a.Id);
            var dto = new AgentDto(a.Id, a.Name, online, online ? "online" : "offline", a.LastSeenAt, a.AgentVersion, a.DriverVersion);
            await ui.Clients.Group(UiHub.TenantGroup(agent.TenantId)).SendAsync("AgentUpdated", dto);
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }

    public Task<BatchAck> ReadBatch(ReadActionBatch batch) =>
        ingest.IngestReadsAsync(Context.User!.Agent(), batch, Context.ConnectionAborted);
}
