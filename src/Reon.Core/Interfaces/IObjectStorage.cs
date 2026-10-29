namespace Reon.Core.Interfaces;

public sealed record ObjectStoragePutRequest(
    string ObjectKey,
    string ContentType,
    long ContentLength,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record StoredObject(
    string ObjectKey,
    string? ETag,
    long Size,
    string? VersionId);

public sealed record StoredMultipartUpload(
    string ObjectKey,
    string StorageUploadId,
    DateTimeOffset InitiatedAt);

public sealed record StoredPart(
    int PartNumber,
    string ETag,
    long Size);

public sealed record MultipartPartETag(
    int PartNumber,
    string ETag);

public sealed record ObjectInfo(
    string ObjectKey,
    long Size,
    string? ETag,
    string? VersionId,
    DateTimeOffset? LastModified,
    IReadOnlyDictionary<string, string> Metadata,
    string? ContentType = null);

/// <summary>
/// Абстракция над объектным хранилищем. Реализации не должны протаскивать
/// типы провайдера (AWS SDK, Swift и т.п.) в остальное приложение.
/// </summary>
public interface IObjectStorage
{
    /// <summary>Логический бакет/контейнер, в котором хранятся объекты.</summary>
    string BucketName { get; }

    Task<StoredObject> PutAsync(
        ObjectStoragePutRequest request,
        Stream content,
        CancellationToken cancellationToken);

    Task<StoredObject> CopyAsync(
        string sourceObjectKey,
        string destinationObjectKey,
        string contentType,
        long contentLength,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken);

    Task<StoredMultipartUpload> InitiateMultipartUploadAsync(
        string objectKey,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken);

    Task<StoredPart> UploadPartAsync(
        string objectKey,
        string storageUploadId,
        int partNumber,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken);

    Task<StoredObject> CompleteMultipartUploadAsync(
        string objectKey,
        string storageUploadId,
        IReadOnlyList<MultipartPartETag> parts,
        CancellationToken cancellationToken);

    Task AbortMultipartUploadAsync(
        string objectKey,
        string storageUploadId,
        CancellationToken cancellationToken);

    /// <summary>Возвращает null, если объекта нет.</summary>
    Task<ObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>Возвращает null, если объекта нет.</summary>
    Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>Идемпотентно: удаление отсутствующего объекта не считается ошибкой.</summary>
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);

    IAsyncEnumerable<ObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort очистка multipart-загрузок, которые так и не завершились.
    /// Реализации, которые не умеют их перечислять, должны залогировать это и вернуть управление.
    /// </summary>
    Task AbortIncompleteUploadsAsync(TimeSpan olderThan, CancellationToken cancellationToken);
}
