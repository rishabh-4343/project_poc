namespace Auditor.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string BackendUrl { get; set; } = "";

    // Sent as X-Agent-Key; set it with the Agent__ApiKey environment variable, never in a committed file.
    public string ApiKey { get; set; } = "";

    public Guid ServerId { get; set; }

    public Guid TenantId { get; set; }

    // Environment variables such as %ProgramData% are expanded; see ResolvedDataDirectory.
    public string DataDirectory { get; set; } = "";

    public int BatchMaxActions { get; set; } = 1000;

    public int BatchMaxMs { get; set; } = 2000;

    public int OutboxMaxMegabytes { get; set; } = 2048;

    public int LogRetentionDays { get; set; } = 14;

    // "Simulated" until the kernel driver exists; no other source is implemented yet.
    public string DriverSource { get; set; } = "Simulated";

    public int SimulatedIntervalMs { get; set; } = 500;

    // Repeated writes to one file by one user inside this window become a single Modified action.
    public int ModifyMergeSeconds { get; set; } = 5;

    public int HeartbeatSeconds { get; set; } = 30;

    public string ResolvedDataDirectory => Path.GetFullPath(Environment.ExpandEnvironmentVariables(DataDirectory));

    // Problems that stop the agent doing useful work; reported as warnings at startup so a fresh checkout still runs.
    public IEnumerable<string> Problems()
    {
        if (!Uri.TryCreate(BackendUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            yield return "BackendUrl must be an absolute http(s) URL.";
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return "ApiKey is empty (set Agent__ApiKey).";
        }

        if (ServerId == Guid.Empty)
        {
            yield return "ServerId is not set (register the server in the backend and set Agent__ServerId).";
        }

        if (TenantId == Guid.Empty)
        {
            yield return "TenantId is not set.";
        }

        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            yield return "DataDirectory is empty.";
        }

        if (BatchMaxActions is < 1 or > 10_000)
        {
            yield return "BatchMaxActions must be 1-10000 (the backend rejects larger batches).";
        }

        if (BatchMaxMs < 100)
        {
            yield return "BatchMaxMs must be at least 100.";
        }
    }
}
