using Microsoft.Extensions.Options;

namespace Auditor.Agent;

// Logs the identity and configuration problems at start and stop; the pipeline runs in its own hosted services.
public sealed class AgentWorker(IOptions<AgentOptions> options, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        logger.LogInformation(
            "agent started: backend {BackendUrl}, server {ServerId}, data {DataDirectory}",
            o.BackendUrl, o.ServerId, o.ResolvedDataDirectory);

        foreach (var problem in o.Problems())
        {
            logger.LogWarning("Configuration: {Problem}", problem);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        logger.LogInformation("agent stopping");
    }
}
