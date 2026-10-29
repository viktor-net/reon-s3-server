namespace Reon.Infrastructure.Persistence;

/// <summary>
/// Quota guard over the administrative bucket settings. Usage comes from the
/// object store through a short-lived cache, so the guard is best-effort.
/// </summary>
public sealed class StorageQuotaGuard(
    IBucketSettingsRepository settings,
    IObjectStorageAdmin storageAdmin) : IStorageQuotaGuard
{
    public async Task EnsureCanWriteAsync(
        string bucket,
        long additionalBytes,
        CancellationToken cancellationToken)
    {
        var bucketSettings = await settings.GetAsync(bucket, cancellationToken);

        if (bucketSettings?.QuotaBytes is not > 0)
        {
            return;
        }

        var stats = await storageAdmin.GetUsageAsync(bucket, refresh: false, cancellationToken);
        var projected = stats.UsedBytes + additionalBytes;

        if (projected > bucketSettings.QuotaBytes)
        {
            throw new QuotaExceededException(bucket, projected, bucketSettings.QuotaBytes.Value);
        }
    }
}
