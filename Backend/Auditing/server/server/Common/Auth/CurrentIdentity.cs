namespace server.Common.Auth;

public interface ICurrentTenant
{
    Guid TenantId { get; }
}

public interface ICurrentUser
{
    Guid UserId { get; }
    string Email { get; }
    string Role { get; }
}
