namespace Reon.Infrastructure.Persistence;

/// <summary>
/// Заполняет реестр возможностей базовыми значениями из конфигурации при
/// старте сервиса. Существующие выученные ограничения не перетираются.
/// </summary>
public sealed partial class StorageCapabilitySeeder(
    IServiceScopeFactory scopeFactory,
    ILogger<StorageCapabilitySeeder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IStorageCapabilityStore>();
            await store.EnsureSeededAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSeedingFailed(logger, exception);
        }
    }

    [LoggerMessage(
        EventId = 2601,
        Level = LogLevel.Warning,
        Message = "Failed to seed storage capabilities from configuration.")]
    private static partial void LogSeedingFailed(ILogger logger, Exception exception);
}
