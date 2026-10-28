using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using server.Common.Auth;
using server.Modules.Wfsa.Data;

namespace server.Common.Hubs;

// Clients join session:{id} after JoinSession and receive ActionsAppended(sessionId, rows); tenant:{id} is joined on connect.
public sealed class UiHub(ICurrentTenant tenant, WfsaDbContext db) : Hub
{
    public static string TenantGroup(Guid tenantId) => $"tenant:{tenantId}";

    public static string SessionGroup(Guid sessionId) => $"session:{sessionId}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenant.TenantId));
        await base.OnConnectedAsync();
    }

    public async Task JoinSession(Guid sessionId)
    {
        if (!await db.Sessions.AnyAsync(s => s.Id == sessionId && s.TenantId == tenant.TenantId, Context.ConnectionAborted))
        {
            throw new HubException("Unknown session.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
    }

    public Task LeaveSession(Guid sessionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
}
