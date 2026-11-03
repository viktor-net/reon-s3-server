namespace Reon.Infrastructure.Persistence;

/// <summary>
/// Реестр возможностей провайдера в Postgres. Базовые значения берутся из
/// <see cref="S3Options.Capabilities"/>, а фактические ограничения
/// запоминаются при ошибке 501.
/// </summary>
public sealed class StorageCapabilityStore(
    ReonDbContext dbContext,
    IOptions<S3Options> s3Options,
    IClock clock) : IStorageCapabilityStore
{
    public async Task<StorageCapabilities> GetAsync(CancellationToken cancellationToken)
    {
        var records = await dbContext.StorageCapabilities
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var values = records.ToDictionary(
            record => record.Name,
            record => record.Supported,
            StringComparer.Ordinal);

        var defaults = s3Options.Value.Capabilities;

        return new StorageCapabilities(
            GetValue(values, StorageCapability.Versioning, defaults.Versioning),
            GetValue(values, StorageCapability.ObjectTags, defaults.ObjectTags),
            GetValue(values, StorageCapability.Lifecycle, defaults.Lifecycle),
            GetValue(values, StorageCapability.BucketPolicy, defaults.BucketPolicy),
            GetValue(values, StorageCapability.ObjectLock, defaults.ObjectLock));
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        var records = await dbContext.StorageCapabilities
            .ToListAsync(cancellationToken);

        var existing = records.ToDictionary(
            record => record.Name,
            StringComparer.Ordinal);

        var now = clock.UtcNow;

        foreach (var capability in Enum.GetValues<StorageCapability>())
        {
            var key = capability.ToApiValue();
            var configured = GetConfiguredValue(capability);

            if (!existing.TryGetValue(key, out var record))
            {
                dbContext.StorageCapabilities.Add(
                    StorageCapabilityRecord.Create(
                        key,
                        configured,
                        StorageCapabilityRecord.SourceConfig,
                        details: null,
                        now));
                continue;
            }

            // Выученное ограничение не перетирается конфигом: иначе UI снова
            // покажет действие, которое провайдер уже отверг.
            if (record.Source == StorageCapabilityRecord.SourceConfig &&
                record.Supported != configured)
            {
                record.Update(
                    configured,
                    StorageCapabilityRecord.SourceConfig,
                    details: null,
                    now);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkUnsupportedAsync(
        StorageCapability capability,
        string? details,
        CancellationToken cancellationToken)
    {
        var key = capability.ToApiValue();
        var record = await dbContext.StorageCapabilities
            .FirstOrDefaultAsync(item => item.Name == key, cancellationToken);

        var now = clock.UtcNow;

        if (record is null)
        {
            dbContext.StorageCapabilities.Add(
                StorageCapabilityRecord.Create(
                    key,
                    supported: false,
                    StorageCapabilityRecord.SourceLearned,
                    details,
                    now));
        }
        else if (record.Supported || record.Source != StorageCapabilityRecord.SourceLearned)
        {
            record.Update(
                supported: false,
                StorageCapabilityRecord.SourceLearned,
                details,
                now);
        }
        else
        {
            return;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool GetValue(
        Dictionary<string, bool> values,
        StorageCapability capability,
        bool fallback)
    {
        return values.TryGetValue(capability.ToApiValue(), out var value)
            ? value
            : fallback;
    }

    private bool GetConfiguredValue(StorageCapability capability)
    {
        var capabilities = s3Options.Value.Capabilities;

        return capability switch
        {
            StorageCapability.Versioning => capabilities.Versioning,
            StorageCapability.ObjectTags => capabilities.ObjectTags,
            StorageCapability.Lifecycle => capabilities.Lifecycle,
            StorageCapability.BucketPolicy => capabilities.BucketPolicy,
            StorageCapability.ObjectLock => capabilities.ObjectLock,
            _ => false,
        };
    }
}
