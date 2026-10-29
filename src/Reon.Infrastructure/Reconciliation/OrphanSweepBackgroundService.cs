namespace Reon.Infrastructure.Reconciliation;

public sealed partial class OrphanSweepBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<FileStorageOptions> options,
    ILogger<OrphanSweepBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.OrphanSweepEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.OrphanSweepIntervalMinutes));

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var reconciliation = scope.ServiceProvider.GetRequiredService<IReconciliationService>();
                await reconciliation.SweepOrphansAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Штатное завершение.
            }
            catch (Exception exception)
            {
                LogOrphanSweepFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(EventId = 2401, Level = LogLevel.Error, Message = "Orphan object sweep failed.")]
    private static partial void LogOrphanSweepFailed(ILogger logger, Exception exception);
}
