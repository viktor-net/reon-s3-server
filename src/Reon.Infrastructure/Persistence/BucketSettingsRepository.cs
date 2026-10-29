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

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
