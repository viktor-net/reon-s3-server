namespace Reon.Core.Interfaces;

public interface IBucketSettingsRepository
{
    Task<BucketSettings?> GetAsync(string bucket, CancellationToken cancellationToken);

    Task<IReadOnlyList<BucketSettings>> ListAsync(CancellationToken cancellationToken);

    Task AddAsync(BucketSettings settings, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
