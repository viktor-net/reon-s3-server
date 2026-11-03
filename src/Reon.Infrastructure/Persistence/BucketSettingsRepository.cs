namespace Reon.Infrastructure.Persistence;

/// <summary>
/// Репозиторий поверх Postgres для таблицы bucket_settings.
/// </summary>
/// <param name="dbContext">Контекст EF Core с нужным DbSet.</param>
public sealed class BucketSettingsRepository(ReonDbContext dbContext) : IBucketSettingsRepository
{
    public Task<BucketSettings?> GetAsync(string bucket, CancellationToken cancellationToken)
    {
        return dbContext.BucketSettings
            .FirstOrDefaultAsync(settings => settings.Bucket == bucket, cancellationToken);
    }

    public async Task<IReadOnlyList<BucketSettings>> ListAsync(CancellationToken cancellationToken)
    {
        return await dbContext.BucketSettings
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(BucketSettings settings, CancellationToken cancellationToken)
    {
        await dbContext.BucketSettings.AddAsync(settings, cancellationToken);
    }

    public async Task DeleteAsync(string bucket, CancellationToken cancellationToken)
    {
        var settings = await dbContext.BucketSettings
            .FirstOrDefaultAsync(item => item.Bucket == bucket, cancellationToken);

        if (settings is not null)
        {
            dbContext.BucketSettings.Remove(settings);
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
