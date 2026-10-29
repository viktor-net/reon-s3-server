using Reon.Core.Entities;
using Reon.Core.Enums;
using Reon.Core.Interfaces;

namespace Reon.Infrastructure.Persistence;

public sealed class MultipartUploadRepository(ReonDbContext dbContext) : IMultipartUploadRepository
{
    public async Task AddAsync(MultipartUpload upload, CancellationToken cancellationToken)
    {
        await dbContext.MultipartUploads.AddAsync(upload, cancellationToken);
    }

    public Task<MultipartUpload?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return dbContext.MultipartUploads.FirstOrDefaultAsync(upload => upload.Id == id, cancellationToken);
    }

    public Task<MultipartUploadPart?> GetPartAsync(
        Guid uploadId,
        int partNumber,
        CancellationToken cancellationToken)
    {
        return dbContext.MultipartUploadParts.FirstOrDefaultAsync(
            part => part.UploadId == uploadId && part.PartNumber == partNumber,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MultipartUploadPart>> GetPartsAsync(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        return await dbContext.MultipartUploadParts
            .Where(part => part.UploadId == uploadId)
            .OrderBy(part => part.PartNumber)
            .ToListAsync(cancellationToken);
    }

    public void AddPart(MultipartUploadPart part)
    {
        dbContext.MultipartUploadParts.Add(part);
    }

    public async Task<IReadOnlyList<MultipartUpload>> GetActiveAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var activeStatuses = new[]
        {
            MultipartUploadStatus.Initiated,
            MultipartUploadStatus.InProgress,
            MultipartUploadStatus.Completing,
        };

        return await dbContext.MultipartUploads
            .AsNoTracking()
            .Where(upload => activeStatuses.Contains(upload.Status))
            .OrderByDescending(upload => upload.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MultipartUpload>> GetExpiredAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        var activeStatuses = new[]
        {
            MultipartUploadStatus.Initiated,
            MultipartUploadStatus.InProgress,
            MultipartUploadStatus.Completing,
        };

        return await dbContext.MultipartUploads
            .Where(upload =>
                activeStatuses.Contains(upload.Status) &&
                upload.ExpiresAt <= now)
            .OrderBy(upload => upload.ExpiresAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return dbContext.SaveChangesAsync(cancellationToken);
    }
}
