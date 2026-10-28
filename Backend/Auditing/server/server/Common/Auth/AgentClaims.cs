using System.Security.Claims;

namespace server.Common.Auth;

public readonly record struct AgentIdentity(Guid AgentId, Guid TenantId);

public static class AgentClaimsExtensions
{
    public static AgentIdentity Agent(this ClaimsPrincipal principal) => new(
        Guid.Parse(principal.FindFirstValue(AgentKeyAuthHandler.AgentIdClaim)!),
        Guid.Parse(principal.FindFirstValue(AgentKeyAuthHandler.TenantIdClaim)!));
}
