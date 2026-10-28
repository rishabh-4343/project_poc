namespace server.Common.Auth;

// Fixed ids; B0.6 seeds rows with these same ids.
public static class DevIdentity
{
    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public const string TenantSlug = "dev";
    public const string Email = "dev@auditor.local";
    public const string DisplayName = "Dev Admin";
    public const string Role = "admin";
}

public sealed class DevCurrentTenant : ICurrentTenant
{
    public Guid TenantId => DevIdentity.TenantId;
}

public sealed class DevCurrentUser : ICurrentUser
{
    public Guid UserId => DevIdentity.UserId;
    public string Email => DevIdentity.Email;
    public string Role => DevIdentity.Role;
}
