namespace Reon.Infrastructure.Retention;

/// <summary>
/// Периодически запускает очистку файлов по сроку хранения. Интервал и размер
/// батча задаются в FileStorageOptions; конкретные строки выбирает
/// <see cref="RetentionGcService"/>.
/// </summary>
public sealed partial class RetentionBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<FileStorageOptions> options,
    ILogger<RetentionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(
            Math.Max(1, options.Value.RetentionSweepIntervalMinutes));

        try
        {
            // Дать приложению подняться и применить миграции.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);

        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var gc = scope.ServiceProvider.GetRequiredService<RetentionGcService>();

            await gc.ExpireDueAsync(cancellationToken);
            await gc.DeleteExpiredAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Штатное завершение.
        }
        catch (Exception exception)
        {
            LogSweepFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 5101, Level = LogLevel.Error, Message = "Retention background sweep failed.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
