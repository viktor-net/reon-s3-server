using System.Runtime.CompilerServices;

namespace Reon.UnitTests.Fakes;

public sealed class FakeObjectStorage : IObjectStorage
{
    public Dictionary<string, StoredObjectState> Objects { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, FakeMultipartState> MultipartUploads { get; } = new(StringComparer.Ordinal);

    public bool FailOnPut { get; set; }

    public string BucketName => "test-bucket";

    public Task<StoredObject> PutAsync(
        ObjectStoragePutRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (FailOnPut)
        {
            throw new ObjectStorageException("Simulated S3 failure.");
        }

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();

        var stored = new StoredObjectState(
            bytes,
            request.ContentType,
            (request.Metadata ?? new Dictionary<string, string>())
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

        Objects[request.ObjectKey] = stored;

        return Task.FromResult(
            new StoredObject(
                request.ObjectKey,
                "\"etag\"",
                bytes.Length,
                null));
    }

    public Task<StoredObject> CopyAsync(
        string sourceObjectKey,
        string destinationObjectKey,
        string contentType,
        long contentLength,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        if (!Objects.TryGetValue(sourceObjectKey, out var source))
        {
            throw new ObjectStorageException($"Object '{sourceObjectKey}' was not found.");
        }

        Objects[destinationObjectKey] = new StoredObjectState(
            source.Bytes.ToArray(),
            contentType,
            (metadata ?? source.Metadata)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

        return Task.FromResult(
            new StoredObject(destinationObjectKey, "\"copy-etag\"", contentLength, null));
    }

    public Task<StoredMultipartUpload> InitiateMultipartUploadAsync(
        string objectKey,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        var uploadId = Guid.NewGuid().ToString("N");
        MultipartUploads[uploadId] = new FakeMultipartState(
            objectKey,
            contentType,
            (metadata ?? new Dictionary<string, string>())
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));

        return Task.FromResult(
            new StoredMultipartUpload(objectKey, uploadId, DateTimeOffset.UtcNow));
    }

    public Task<StoredPart> UploadPartAsync(
        string objectKey,
        string storageUploadId,
        int partNumber,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        if (!MultipartUploads.TryGetValue(storageUploadId, out var upload))
        {
            throw new ObjectStorageException($"Multipart upload '{storageUploadId}' was not found.");
        }

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();
        upload.Parts[partNumber] = bytes;

        return Task.FromResult(
            new StoredPart(partNumber, $"\"part-{partNumber}\"", bytes.Length));
    }

    public Task<StoredObject> CompleteMultipartUploadAsync(
        string objectKey,
        string storageUploadId,
        IReadOnlyList<MultipartPartETag> parts,
        CancellationToken cancellationToken)
    {
        if (!MultipartUploads.Remove(storageUploadId, out var upload))
        {
            throw new ObjectStorageException($"Multipart upload '{storageUploadId}' was not found.");
        }

        var bytes = parts
            .OrderBy(part => part.PartNumber)
            .SelectMany(part => upload.Parts[part.PartNumber])
            .ToArray();

        Objects[objectKey] = new StoredObjectState(bytes, upload.ContentType, upload.Metadata);
        return Task.FromResult(new StoredObject(objectKey, "\"multipart-etag\"", bytes.Length, null));
    }

    public Task AbortMultipartUploadAsync(
        string objectKey,
        string storageUploadId,
        CancellationToken cancellationToken)
    {
        MultipartUploads.Remove(storageUploadId);
        return Task.CompletedTask;
    }

    public Task<ObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        if (!Objects.TryGetValue(objectKey, out var stored))
        {
            return Task.FromResult<ObjectInfo?>(null);
        }

        return Task.FromResult<ObjectInfo?>(
            new ObjectInfo(
                objectKey,
                stored.Bytes.Length,
                "\"etag\"",
                null,
                DateTimeOffset.UtcNow,
                stored.Metadata));
    }

    public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        if (!Objects.TryGetValue(objectKey, out var stored))
        {
            return Task.FromResult<Stream?>(null);
        }

        return Task.FromResult<Stream?>(new MemoryStream(stored.Bytes, writable: false));
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        Objects.Remove(objectKey);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ObjectInfo> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var (key, stored) in Objects)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            yield return new ObjectInfo(
                key,
                stored.Bytes.Length,
                "\"etag\"",
                null,
                DateTimeOffset.UtcNow,
                stored.Metadata);
        }

        await Task.CompletedTask;
    }

    public Task AbortIncompleteUploadsAsync(TimeSpan olderThan, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public sealed class FakeMultipartState(
        string objectKey,
        string contentType,
        IReadOnlyDictionary<string, string> metadata)
    {
        public string ObjectKey { get; } = objectKey;

        public string ContentType { get; } = contentType;

        public IReadOnlyDictionary<string, string> Metadata { get; } = metadata;

        public Dictionary<int, byte[]> Parts { get; } = [];
    }

    public sealed record StoredObjectState(
        byte[] Bytes,
        string ContentType,
        IReadOnlyDictionary<string, string> Metadata);
}

public sealed class FakeFileMetadataRepository : IFileMetadataRepository
{
    private readonly List<FileMetadata> _files = [];

    public IReadOnlyList<FileMetadata> Files => _files;
    public Task AddAsync(FileMetadata file, CancellationToken cancellationToken)
    {
        _files.Add(file);
        return Task.CompletedTask;
    }

    public Task<FileMetadata?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return Task.FromResult(_files.FirstOrDefault(file => file.Id == id));
    }

    public Task<FileMetadata?> GetByObjectKeyAsync(string objectKey, CancellationToken cancellationToken)
    {
        return Task.FromResult(_files.FirstOrDefault(file => file.ObjectKey == objectKey));
    }

    public Task<IReadOnlyList<FileMetadata>> GetByStatusOlderThanAsync(
        FileStatus status,
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FileMetadata> result = _files
            .Where(file => file.Status == status && file.UpdatedAt < olderThan)
            .Take(limit)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<FileMetadata>> GetDeletedOlderThanAsync(
        DateTimeOffset olderThan,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FileMetadata> result = _files
            .Where(file =>
                file.Status == FileStatus.Deleted &&
                file.DeletedAt != null &&
                file.DeletedAt < olderThan)
            .Take(limit)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyCollection<Guid>> GetActiveFileIdsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyCollection<Guid>>(
            _files
                .Where(file => file.Status is FileStatus.Pending or FileStatus.Ready or FileStatus.Expired)
                .Select(file => file.Id)
                .ToArray());
    }

    public Task<FileListResult> ListAsync(FileListQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var filtered = _files
            .Where(file => file.Status != FileStatus.Deleted)
            .Where(file => query.Status is null || file.Status == query.Status)
            .Where(file => string.IsNullOrWhiteSpace(query.ContentType) ||
                           string.Equals(file.ContentType, query.ContentType, StringComparison.OrdinalIgnoreCase))
            .Where(file => string.IsNullOrWhiteSpace(query.OriginalName) ||
                           file.OriginalName.Contains(query.OriginalName, StringComparison.OrdinalIgnoreCase))
            .Where(file => query.CreatedFrom is null || file.CreatedAt >= query.CreatedFrom)
            .Where(file => query.CreatedTo is null || file.CreatedAt <= query.CreatedTo)
            .OrderByDescending(file => file.CreatedAt)
            .ToArray();

        var items = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        return Task.FromResult(new FileListResult(items, filtered.Length, page, pageSize));
    }

    public Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var active = _files.Where(file => file.Status != FileStatus.Deleted).ToArray();

        return Task.FromResult(
            new StorageStats(
                active.Length,
                active.Sum(file => file.Size),
                active.GroupBy(file => file.Status)
                    .ToDictionary(group => group.Key.ToString(), group => group.Count()),
                active.GroupBy(file => file.ContentType)
                    .ToDictionary(group => group.Key, group => group.Sum(file => file.Size))));
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
    {
        return Task.FromResult(_files.Any(file => file.Id == id));
    }

    public void Remove(FileMetadata file)
    {
        _files.Remove(file);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

public sealed class FakeMultipartUploadRepository : IMultipartUploadRepository
{
    private readonly List<MultipartUpload> _uploads = [];
    private readonly List<MultipartUploadPart> _parts = [];

    public IReadOnlyList<MultipartUpload> Uploads => _uploads;

    public Task AddAsync(MultipartUpload upload, CancellationToken cancellationToken)
    {
        _uploads.Add(upload);
        return Task.CompletedTask;
    }

    public Task<MultipartUpload?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return Task.FromResult(_uploads.FirstOrDefault(upload => upload.Id == id));
    }

    public Task<MultipartUploadPart?> GetPartAsync(
        Guid uploadId,
        int partNumber,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(_parts.FirstOrDefault(
            part => part.UploadId == uploadId && part.PartNumber == partNumber));
    }

    public Task<IReadOnlyList<MultipartUploadPart>> GetPartsAsync(
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MultipartUploadPart> parts = _parts
            .Where(part => part.UploadId == uploadId)
            .OrderBy(part => part.PartNumber)
            .ToArray();

        return Task.FromResult(parts);
    }

    public void AddPart(MultipartUploadPart part)
    {
        _parts.Add(part);
    }

    public Task<IReadOnlyList<MultipartUpload>> GetActiveAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MultipartUpload> uploads = _uploads
            .Where(upload => upload.Status is MultipartUploadStatus.Initiated or MultipartUploadStatus.InProgress or MultipartUploadStatus.Completing)
            .Take(limit)
            .ToArray();

        return Task.FromResult(uploads);
    }

    public Task<IReadOnlyList<MultipartUpload>> GetExpiredAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MultipartUpload> uploads = _uploads
            .Where(upload =>
                (upload.Status is MultipartUploadStatus.Initiated or MultipartUploadStatus.InProgress or MultipartUploadStatus.Completing) &&
                upload.ExpiresAt <= now)
            .Take(limit)
            .ToArray();

        return Task.FromResult(uploads);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
