using Microsoft.EntityFrameworkCore;
using Reon.Core.Entities;
using Reon.Core.Enums;
using Reon.Core.Interfaces;

namespace Reon.Infrastructure.Persistence;

public sealed class FileMetadataRepository(ReonDbContext dbContext) : IFileMetadataRepository
{
    public async Task AddAsync(FileMetadata file, CancellationToken cancellationToken)
    {
        await dbContext.Files.AddAsync(file, cancellationToken);
    }

    public Task<FileMetadata?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return dbContext.Files.FirstOrDefaultAsync(file => file.Id == id, cancellationToken);
    }

    public Task<FileMetadata?> GetByObjectKeyAsync(string objectKey, CancellationToken cancellationToken)
    {
        return dbContext.Files.FirstOrDefaultAsync(file => file.ObjectKey == objectKey, cancellationToken);
    }

    public async Task<IReadOnlyList<FileMetadata>> GetByStatusOlderThanAsync(
        FileStatus status,
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken)
    {
        return await dbContext.Files
            .Where(file => file.Status == status && file.UpdatedAt < olderThan)
            .OrderBy(file => file.UpdatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FileMetadata>> GetDeletedOlderThanAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken)
    {
        return await dbContext.Files
            .Where(file =>
                file.Status == FileStatus.Deleted &&
                file.DeletedAt != null &&
                file.DeletedAt < olderThan)
            .OrderBy(file => file.DeletedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<Guid>> GetActiveFileIdsAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Files
            .AsNoTracking()
            .Where(file => file.Status == FileStatus.Pending || file.Status == FileStatus.Ready)
            .Select(file => file.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
    {
        return dbContext.Files.AnyAsync(file => file.Id == id, cancellationToken);
    }

    public void Remove(FileMetadata file)
    {
        dbContext.Files.Remove(file);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
