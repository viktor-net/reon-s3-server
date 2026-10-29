namespace Reon.Infrastructure.Reconciliation;

/// <summary>
/// Периодически запускает сверку метаданных и объектного хранилища.
/// </summary>
/// <param name="scopeFactory">Создаёт scope для scoped-сервиса сверки.</param>
/// <param name="objectStorage">Прерывает незавершённые multipart-загрузки.</param>
/// <param name="options">Интервал сверки и связанные таймауты.</param>
/// <param name="logger">Лог сбоев прохода.</param>
public sealed partial class ReconciliationBackgroundService(
    IServiceScopeFactory scopeFactory,
    IObjectStorage objectStorage,
    IOptions<FileStorageOptions> options,
    ILogger<ReconciliationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ReconciliationIntervalMinutes));

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
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
            var reconciliation = scope.ServiceProvider.GetRequiredService<IReconciliationService>();

            await reconciliation.ReconcilePendingAsync(cancellationToken);
            await reconciliation.ReconcileDeletingAsync(cancellationToken);
            await reconciliation.PurgeDeletedAsync(cancellationToken);
            await objectStorage.AbortIncompleteUploadsAsync(
                TimeSpan.FromDays(Math.Max(1, options.Value.MultipartUploadLifetimeDays)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Штатное завершение.
        }
        catch (Exception exception)
        {
            LogReconciliationFailed(logger, exception);
        }
    }

    [LoggerMessage(EventId = 2301, Level = LogLevel.Error, Message = "Background reconciliation iteration failed.")]
    private static partial void LogReconciliationFailed(ILogger logger, Exception exception);
}
