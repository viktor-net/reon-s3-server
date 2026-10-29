namespace Reon.Core.Interfaces;

public interface IFileMetadataRepository
{
    Task AddAsync(FileMetadata file, CancellationToken cancellationToken);

    Task<FileMetadata?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<FileMetadata?> GetByObjectKeyAsync(string objectKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<FileMetadata>> GetByStatusOlderThanAsync(
        FileStatus status,
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FileMetadata>> GetDeletedOlderThanAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>File ids whose objects are allowed to exist (Pending or Ready).</summary>
    Task<IReadOnlyCollection<Guid>> GetActiveFileIdsAsync(CancellationToken cancellationToken);

    Task<FileListResult> ListAsync(FileListQuery query, CancellationToken cancellationToken);

    Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);

    void Remove(FileMetadata file);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
