namespace Reon.Infrastructure.Reconciliation;

/// <summary>
/// Периодически завершает просроченные сессии параллельной загрузки файла
/// по частям (multipart-загрузки) в Postgres и S3.
/// </summary>
/// <param name="scopeFactory">Создаёт scope для scoped-сервиса параллельной
/// загрузки файла по частям (multipart-загрузки).</param>
/// <param name="objectStorage">Прерывает незавершённые загрузки на стороне S3.</param>
/// <param name="options">Срок жизни сессии и интервал очистки.</param>
/// <param name="logger">Лог сбоев очистки.</param>
public sealed partial class MultipartCleanupBackgroundService(
    IServiceScopeFactory scopeFactory,
    IObjectStorage objectStorage,
    IOptions<FileStorageOptions> options,
    ILogger<MultipartCleanupBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lifetime = TimeSpan.FromDays(Math.Max(1, options.Value.MultipartUploadLifetimeDays));
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.MultipartCleanupIntervalMinutes));

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
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
                var uploads = scope.ServiceProvider.GetRequiredService<IMultipartUploadService>();
                await uploads.CleanupExpiredAsync(stoppingToken);
                await objectStorage.AbortIncompleteUploadsAsync(lifetime, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Штатное завершение.
            }
            catch (Exception exception)
            {
                LogCleanupFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(EventId = 2501, Level = LogLevel.Error, Message = "Multipart upload cleanup iteration failed.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
