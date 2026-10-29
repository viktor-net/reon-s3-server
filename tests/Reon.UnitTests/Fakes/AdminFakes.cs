using System.Text.Json;

namespace Reon.UnitTests.Fakes;

public sealed class FakeObjectStorageAdmin : IObjectStorageAdmin
{
    public Dictionary<string, DateTimeOffset?> Buckets { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Dictionary<string, byte[]>> Objects { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Dictionary<string, string>> Tags { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string?> Policies { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<ObjectVersion>> Versions { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, bool> Versioning { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IReadOnlyList<LifecycleRule>> Lifecycle { get; } = new(StringComparer.Ordinal);

    public bool FailQuota { get; set; }

    public bool NotSupportedPolicy { get; set; }

    public bool NotSupportedTagging { get; set; }

    public Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StorageBucket> result = Buckets
            .Select(pair => new StorageBucket(pair.Key, pair.Value))
            .OrderBy(bucket => bucket.Name, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<StorageBucket?> GetBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            Buckets.TryGetValue(bucket, out var createdAt)
                ? new StorageBucket(bucket, createdAt)
                : null);
    }

    public Task CreateBucketAsync(
        string bucket,
        bool versioningEnabled,
        bool objectLockEnabled,
        CancellationToken cancellationToken)
    {
        Buckets[bucket] = DateTimeOffset.UtcNow;
        Objects[bucket] = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        Versioning[bucket] = versioningEnabled || objectLockEnabled;
        return Task.CompletedTask;
    }

    public Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        Buckets.Remove(bucket);
        Objects.Remove(bucket);
        Policies.Remove(bucket);
        Versioning.Remove(bucket);
        Lifecycle.Remove(bucket);
        return Task.CompletedTask;
    }

    public Task<BucketVersioning> GetVersioningAsync(string bucket, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            new BucketVersioning(Versioning.GetValueOrDefault(bucket), Suspended: false));
    }

    public Task SetVersioningAsync(string bucket, bool enabled, CancellationToken cancellationToken)
    {
        Versioning[bucket] = enabled;
        return Task.CompletedTask;
    }

    public Task<BucketSecurity> GetSecurityAsync(string bucket, CancellationToken cancellationToken)
    {
        return Task.FromResult(new BucketSecurity(ObjectLockEnabled: false, Encryption: "AES256"));
    }

    public Task<string?> GetBucketPolicyAsync(string bucket, CancellationToken cancellationToken)
    {
        return Task.FromResult(Policies.GetValueOrDefault(bucket));
    }

    public Task SetBucketPolicyAsync(string bucket, string? policy, CancellationToken cancellationToken)
    {
        if (NotSupportedPolicy)
        {
            throw new StorageCapabilityNotSupportedException("bucket policy");
        }

        Policies[bucket] = policy;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LifecycleRule>> GetLifecycleAsync(
        string bucket,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Lifecycle.GetValueOrDefault(bucket) ?? []);
    }

    public Task SetLifecycleAsync(
        string bucket,
        IReadOnlyList<LifecycleRule> rules,
        CancellationToken cancellationToken)
    {
        Lifecycle[bucket] = rules;
        return Task.CompletedTask;
    }

    public Task<ObjectListing> ListObjectsAsync(
        string bucket,
        string? prefix,
        string delimiter,
        int maxKeys,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        var objects = GetBucketObjects(bucket);

        var matching = objects
            .Where(pair => prefix is null || pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Take(maxKeys)
            .Select(pair => ToInfo(bucket, pair.Key, pair.Value))
            .ToArray();

        return Task.FromResult(new ObjectListing([], matching, null));
    }

    public Task<ObjectInfo?> HeadObjectAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        var objects = GetBucketObjects(bucket);

        return Task.FromResult(
            objects.TryGetValue(key, out var content)
                ? ToInfo(bucket, key, content)
                : null);
    }

    public Task<Stream?> OpenReadAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        var objects = GetBucketObjects(bucket);

        return Task.FromResult<Stream?>(
            objects.TryGetValue(key, out var content)
                ? new MemoryStream(content, writable: false)
                : null);
    }

    public Task<ObjectInfo> PutObjectAsync(
        string bucket,
        string key,
        string contentType,
        long length,
        Stream content,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);

        var bytes = buffer.ToArray();
        GetBucketObjects(bucket)[key] = bytes;

        return Task.FromResult(ToInfo(bucket, key, bytes));
    }

    public Task DeleteObjectAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        GetBucketObjects(bucket).Remove(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ObjectVersion>> ListVersionsAsync(
        string bucket,
        string? prefix,
        string? key,
        CancellationToken cancellationToken)
    {
        var versions = Versions.GetValueOrDefault(bucket) ?? [];
        return Task.FromResult<IReadOnlyList<ObjectVersion>>(versions);
    }

    public Task<IReadOnlyDictionary<string, string>> GetTagsAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        var tags = Tags.GetValueOrDefault(bucket) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyDictionary<string, string>>(tags);
    }

    public Task SetTagsAsync(
        string bucket,
        string key,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken)
    {
        if (NotSupportedTagging)
        {
            throw new StorageCapabilityNotSupportedException("object tags");
        }

        Tags[bucket] = tags.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return Task.CompletedTask;
    }

    public string CreatePresignedGetUrl(
        string bucket,
        string key,
        string? versionId,
        DateTimeOffset expiresAt)
    {
        return $"https://storage.example/{bucket}/{key}?expires={expiresAt:O}";
    }

    public Task<BucketUsageStats> GetUsageAsync(
        string bucket,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var objects = GetBucketObjects(bucket);
        var stats = new BucketUsageStats(
            bucket,
            objects.Count,
            objects.Values.Sum(content => (long)content.Length),
            DateTimeOffset.UtcNow);

        return Task.FromResult(stats);
    }

    private Dictionary<string, byte[]> GetBucketObjects(string bucket)
    {
        if (!Objects.TryGetValue(bucket, out var objects))
        {
            objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            Objects[bucket] = objects;
        }

        return objects;
    }

    private static ObjectInfo ToInfo(string bucket, string key, byte[] content)
    {
        return new ObjectInfo(
            key,
            content.Length,
            "\"etag\"",
            null,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "application/octet-stream");
    }
}

public sealed class FakeBucketSettingsRepository : IBucketSettingsRepository
{
    private readonly List<BucketSettings> _settings = [];

    public Task<BucketSettings?> GetAsync(string bucket, CancellationToken cancellationToken)
    {
        return Task.FromResult(_settings.FirstOrDefault(settings => settings.Bucket == bucket));
    }

    public Task<IReadOnlyList<BucketSettings>> ListAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<BucketSettings>>(_settings.ToArray());
    }

    public Task AddAsync(BucketSettings settings, CancellationToken cancellationToken)
    {
        _settings.Add(settings);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class FakeAuditRepository : IAuditRepository
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AddAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<AuditPage> ListAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var items = Entries
            .Where(entry => query.Result is null || entry.Result == query.Result)
            .OrderByDescending(entry => entry.OccurredAt)
            .ToArray();

        return Task.FromResult(new AuditPage(items, items.Length, 1, Math.Max(1, query.PageSize)));
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class FakeStorageEventPublisher : IStorageEventPublisher
{
    public List<StorageEvent> Events { get; } = [];

    public ValueTask PublishAsync(StorageEvent storageEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(storageEvent);
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeStorageAuditor(FakeAuditRepository repository) : IStorageAuditor
{
    public Task RecordAsync(
        string actor,
        string action,
        string resource,
        AuditResult result,
        int? statusCode,
        object? details,
        CancellationToken cancellationToken)
    {
        var entry = AuditEntry.Create(
            Guid.CreateVersion7(),
            actor,
            action,
            resource,
            result,
            statusCode,
            details is null
                ? null
                : JsonSerializer.SerializeToDocument(details, JsonSerializerOptions.Web),
            DateTimeOffset.UtcNow);

        return repository.AddAsync(entry, cancellationToken);
    }
}

public sealed class FakeStorageQuotaGuard(
    FakeBucketSettingsRepository? settings = null,
    FakeObjectStorageAdmin? storage = null) : IStorageQuotaGuard
{
    public List<(string Bucket, long AdditionalBytes)> Calls { get; } = [];

    public long? QuotaBytes { get; set; }

    public long UsedBytes { get; set; }

    public async Task EnsureCanWriteAsync(
        string bucket,
        long additionalBytes,
        CancellationToken cancellationToken)
    {
        Calls.Add((bucket, additionalBytes));

        var quota = QuotaBytes;

        if (quota is null && settings is not null)
        {
            var bucketSettings = await settings.GetAsync(bucket, cancellationToken);
            quota = bucketSettings?.QuotaBytes;
        }

        if (quota is not > 0)
        {
            return;
        }

        var used = storage is not null
            ? await storage.GetUsageAsync(bucket, refresh: false, cancellationToken)
            : new BucketUsageStats(bucket, 0, UsedBytes, DateTimeOffset.UtcNow);

        if (used.UsedBytes + additionalBytes > quota)
        {
            throw new QuotaExceededException(bucket, used.UsedBytes + additionalBytes, quota.Value);
        }
    }
}
