namespace Reon.Api.Events;

/// <summary>
/// Keeps idle WebSocket subscribers alive and lets clients detect a dead link.
/// </summary>
public sealed partial class AdminEventsHeartbeatService(
    AdminEventHub hub,
    IClock clock,
    IOptions<AdminOptions> adminOptions,
    ILogger<AdminEventsHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, adminOptions.Value.EventHeartbeatSeconds));

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await hub.PublishAsync(
                        new StorageEvent(
                            StorageEvent.Heartbeat,
                            clock.UtcNow,
                            new { intervalSeconds = (int)interval.TotalSeconds }),
                        stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogHeartbeatFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
    }

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Admin events heartbeat stopped unexpectedly.")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception exception);
}
