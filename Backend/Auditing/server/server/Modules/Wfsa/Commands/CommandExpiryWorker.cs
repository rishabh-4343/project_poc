namespace server.Modules.Wfsa.Commands;

public sealed class CommandExpiryWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<CommandExpiryWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Wfsa:ExpiryPollSeconds", 5));

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var commands = scope.ServiceProvider.GetRequiredService<CommandService>();
                var stopped = await commands.StopOverdueAsync(stoppingToken);
                if (stopped > 0)
                {
                    logger.LogInformation("Stopping {Count} wfsa session(s) past MaxEndAt", stopped);
                }

                var expired = await commands.ExpireAsync(stoppingToken);
                if (expired > 0)
                {
                    logger.LogInformation("Expired {Count} wfsa command(s)", expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Command expiry pass failed");
            }
        }
    }
}
