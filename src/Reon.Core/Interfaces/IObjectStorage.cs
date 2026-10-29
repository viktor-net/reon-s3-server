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
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Abstraction over the object store. Implementations must not leak
/// provider-specific types (AWS SDK, Swift, etc.) into the rest of the app.
/// </summary>
public interface IObjectStorage
{
    /// <summary>Logical bucket/container that stores the objects.</summary>
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

    /// <summary>Returns null when the object does not exist.</summary>
    Task<ObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>Returns null when the object does not exist.</summary>
    Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken);

    /// <summary>Idempotent: deleting a missing object is not an error.</summary>
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);

    IAsyncEnumerable<ObjectInfo> ListAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort cleanup for multipart uploads that were never completed.
    /// Implementations that cannot enumerate them should log and return.
    /// </summary>
    Task AbortIncompleteUploadsAsync(TimeSpan olderThan, CancellationToken cancellationToken);
}
