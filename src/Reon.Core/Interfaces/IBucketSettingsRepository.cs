namespace Reon.Core.Interfaces;

public interface IBucketSettingsRepository
{
    Task<BucketSettings?> GetAsync(string bucket, CancellationToken cancellationToken);

    Task<IReadOnlyList<BucketSettings>> ListAsync(CancellationToken cancellationToken);

    Task AddAsync(BucketSettings settings, CancellationToken cancellationToken);

    /// <summary>Удаляет настройки бакета. Отсутствующая строка не считается ошибкой.</summary>
    Task DeleteAsync(string bucket, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
