using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using server.Common.Db;

namespace server.Common.Auth;

public static class AgentKeyHasher
{
    public static byte[] Hash(string apiKey) => SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
}

public sealed class AgentKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentKey";
    public const string HeaderName = "X-Agent-Key";
    public const string AgentIdClaim = "agent_id";
    public const string TenantIdClaim = "tenant_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var key) || string.IsNullOrWhiteSpace(key))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = AgentKeyHasher.Hash(key.ToString());
        var db = Context.RequestServices.GetRequiredService<ConfigDbContext>();

        var agent = await db.Agents.AsNoTracking()
            .Where(a => a.ApiKeyHash == hash && a.RevokedAt == null && a.Status != "revoked")
            .Select(a => new { a.Id, a.TenantId })
            .FirstOrDefaultAsync(Context.RequestAborted);

        if (agent is null)
        {
            return AuthenticateResult.Fail("Invalid agent key.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(AgentIdClaim, agent.Id.ToString()), new Claim(TenantIdClaim, agent.TenantId.ToString())],
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
