namespace Reon.Api.Health;

/// <summary>
/// Проверка доступности S3-эндпоинта и учётных данных.
/// </summary>
/// <param name="objectStorage">Объектное хранилище: HEAD к заведомо отсутствующему ключу.</param>
/// <param name="options">Настройки файлового API: префикс ключа для проверки.</param>
public sealed class S3HealthCheck(
    IObjectStorage objectStorage,
    IOptions<FileStorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // HEAD для заведомо несуществующего ключа достаточно, чтобы проверить
            // доступность эндпоинта и приём учётных данных. 404 — это норма.
            await objectStorage.HeadAsync(
                $"{options.Value.KeyPrefix}__healthcheck__",
                cancellationToken);

            return HealthCheckResult.Healthy("S3 endpoint is reachable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("S3 health check failed.", exception);
        }
    }
}
