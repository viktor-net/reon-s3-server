namespace Reon.Infrastructure.Persistence;

/// <summary>
/// Репозиторий поверх Postgres для таблицы files.
/// </summary>
/// <param name="dbContext">Контекст EF Core с нужным DbSet.</param>
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
            .Where(file =>
                file.Status == FileStatus.Pending ||
                file.Status == FileStatus.Ready ||
                file.Status == FileStatus.Expired)
            .Select(file => file.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<FileListResult> ListAsync(FileListQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var files = dbContext.Files
            .AsNoTracking()
            .Where(file => file.Status != FileStatus.Deleted);

        if (query.Status is { } status)
        {
            files = files.Where(file => file.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.ContentType))
        {
            files = files.Where(file => file.ContentType == query.ContentType);
        }

        if (!string.IsNullOrWhiteSpace(query.OriginalName))
        {
            files = files.Where(file => EF.Functions.ILike(file.OriginalName, $"%{query.OriginalName}%"));
        }

        if (query.CreatedFrom is { } createdFrom)
        {
            files = files.Where(file => file.CreatedAt >= createdFrom);
        }

        if (query.CreatedTo is { } createdTo)
        {
            files = files.Where(file => file.CreatedAt <= createdTo);
        }

        var total = await files.CountAsync(cancellationToken);

        files = query.Sort switch
        {
            "createdAt" => files.OrderBy(file => file.CreatedAt),
            "size" => files.OrderBy(file => file.Size),
            "-size" => files.OrderByDescending(file => file.Size),
            "originalName" => files.OrderBy(file => file.OriginalName),
            "-originalName" => files.OrderByDescending(file => file.OriginalName),
            _ => files.OrderByDescending(file => file.CreatedAt),
        };

        var items = await files
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new FileListResult(items, total, page, pageSize);
    }

    public async Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var active = dbContext.Files
            .AsNoTracking()
            .Where(file => file.Status != FileStatus.Deleted);

        var totalFiles = await active.CountAsync(cancellationToken);
        var totalBytes = await active.SumAsync(file => (long?)file.Size, cancellationToken) ?? 0;

        var byStatus = await active
            .GroupBy(file => file.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var byContentType = await active
            .GroupBy(file => file.ContentType)
            .Select(group => new { ContentType = group.Key, Bytes = group.Sum(file => (long?)file.Size) ?? 0 })
            .ToListAsync(cancellationToken);

        return new StorageStats(
            totalFiles,
            totalBytes,
            byStatus.ToDictionary(item => item.Status.ToString(), item => item.Count),
            byContentType.ToDictionary(item => item.ContentType, item => item.Bytes));
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
