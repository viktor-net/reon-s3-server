namespace Reon.Api.Health;

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
            // A HEAD for a key that should not exist is enough to prove that the
            // endpoint is reachable and credentials are accepted. A 404 is healthy.
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
