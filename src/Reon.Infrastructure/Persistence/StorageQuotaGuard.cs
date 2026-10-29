namespace Reon.Infrastructure.Persistence;

/// <summary>
/// Проверка квоты по административным настройкам бакета. Использование берётся
/// из объектного хранилища через короткий кеш, поэтому проверка приблизительная.
/// </summary>
/// <param name="settings">Квота бакета из Postgres; её отсутствие отключает проверку.</param>
/// <param name="storageAdmin">Использование бакета с учётом кеша.</param>
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
