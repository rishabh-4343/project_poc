namespace server.Common;

// The body of every 400/404/409 the API returns by hand, for the OpenAPI contract.
public sealed record ApiError(string Error);
