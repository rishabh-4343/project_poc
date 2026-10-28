using Microsoft.AspNetCore.SignalR;
using server.Common.Hubs;
using server.Modules.Wfsa.Data;

namespace server.Modules.Wfsa.Sessions;

// Slim on purpose: clients merge it by id, and the session's folders are not loaded on every code path.
public sealed record SessionUpdate(
    Guid Id, Guid ServerId, SessionState State, string? FailureReason, DateTimeOffset? ActiveAt, DateTimeOffset? StoppedAt, Guid? StoppedBy);

// Sends SessionUpdated to the tenant group; a failed push never fails the operation that caused it.
public sealed class SessionNotifier(IHubContext<UiHub> ui, ILogger<SessionNotifier> logger)
{
    public async Task PublishAsync(SessionRecord s, CancellationToken ct)
    {
        try
        {
            var update = new SessionUpdate(s.Id, s.ServerId, s.State, s.FailureReason, s.ActiveAt, s.StoppedAt, s.StoppedBy);
            await ui.Clients.Group(UiHub.TenantGroup(s.TenantId)).SendAsync("SessionUpdated", update, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SessionUpdated push failed for session {SessionId}", s.Id);
        }
    }
}
