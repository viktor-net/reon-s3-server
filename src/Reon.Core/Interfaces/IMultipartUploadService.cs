using Reon.Core.Entities;

namespace Reon.Core.Interfaces;

public interface IMultipartUploadService
{
    Task<MultipartUpload> InitiateAsync(
        string originalName,
        string? contentType,
        string? description,
        IReadOnlyDictionary<string, string>? tags,
        CancellationToken cancellationToken,
        int? retentionSeconds = null);

    Task<MultipartUploadPart> UploadPartAsync(
        Guid uploadId,
        int partNumber,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken);

    Task<FileMetadata> CompleteAsync(Guid uploadId, CancellationToken cancellationToken);

    Task AbortAsync(Guid uploadId, CancellationToken cancellationToken);

    Task<(MultipartUpload Upload, IReadOnlyList<MultipartUploadPart> Parts)> GetAsync(
        Guid uploadId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MultipartUpload>> ListActiveAsync(
        int limit,
        CancellationToken cancellationToken);

    Task CleanupExpiredAsync(CancellationToken cancellationToken);
}
