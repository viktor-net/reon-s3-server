namespace Reon.Core.Interfaces;

public interface IMultipartUploadRepository
{
    Task AddAsync(MultipartUpload upload, CancellationToken cancellationToken);

    Task<MultipartUpload?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<MultipartUploadPart?> GetPartAsync(
        Guid uploadId,
        int partNumber,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MultipartUploadPart>> GetPartsAsync(
        Guid uploadId,
        CancellationToken cancellationToken);

    void AddPart(MultipartUploadPart part);

    Task<IReadOnlyList<MultipartUpload>> GetActiveAsync(
        int limit,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MultipartUpload>> GetExpiredAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
