using System.Runtime.CompilerServices;

namespace Reon.UnitTests.Fakes;

public sealed class FakeObjectStorage : IObjectStorage
{
    public Dictionary<string, StoredObjectState> Objects { get; } = new(StringComparer.Ordinal);

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
                .Where(file => file.Status is FileStatus.Pending or FileStatus.Ready)
                .Select(file => file.Id)
                .ToArray());
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
