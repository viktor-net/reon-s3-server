using System.Collections.Concurrent;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using CoreBucketAlreadyExistsException = Reon.Core.Exceptions.BucketAlreadyExistsException;
using CoreLifecycleRule = Reon.Core.Models.LifecycleRule;

namespace Reon.Infrastructure.Storage;

/// <summary>
/// Administrative S3 operations across buckets. Capabilities that are optional
/// for S3-compatible providers (bucket policy, lifecycle, object lock) degrade
/// gracefully on read and raise a domain exception on write when unsupported.
/// </summary>
public sealed partial class S3ObjectStorageAdmin : IObjectStorageAdmin
{
    private readonly IAmazonS3 _s3;
    private readonly S3Options _s3Options;
    private readonly AdminOptions _adminOptions;
    private readonly ILogger<S3ObjectStorageAdmin> _logger;
    private readonly ConcurrentDictionary<string, UsageCacheEntry> _usageCache = new(StringComparer.Ordinal);

    public S3ObjectStorageAdmin(
        IAmazonS3 s3,
        IOptions<S3Options> s3Options,
        IOptions<AdminOptions> adminOptions,
        ILogger<S3ObjectStorageAdmin> logger)
    {
        _s3 = s3;
        _s3Options = s3Options.Value;
        _adminOptions = adminOptions.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.ListBucketsAsync(cancellationToken);

            return (response.Buckets ?? [])
                .Select(bucket => new StorageBucket(
                    bucket.BucketName,
                    bucket.CreationDate is { } createdAt
                        ? new DateTimeOffset(createdAt.ToUniversalTime(), TimeSpan.Zero)
                        : null))
                .OrderBy(bucket => bucket.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, "list buckets");
        }
    }

    public async Task<StorageBucket?> GetBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        var buckets = await ListBucketsAsync(cancellationToken);
        return buckets.FirstOrDefault(item =>
            string.Equals(item.Name, bucket, StringComparison.Ordinal));
    }

    public async Task CreateBucketAsync(
        string bucket,
        bool versioningEnabled,
        bool objectLockEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            await _s3.PutBucketAsync(
                new PutBucketRequest
                {
                    BucketName = bucket,
                    ObjectLockEnabledForBucket = objectLockEnabled,
                },
                cancellationToken);

            if (versioningEnabled)
            {
                try
                {
                    await SetVersioningAsync(bucket, enabled: true, cancellationToken);
                }
                catch (StorageCapabilityNotSupportedException)
                {
                    await TryDeleteCreatedBucketAsync(bucket, cancellationToken);
                    throw;
                }
                catch (ObjectStorageException)
                {
                    await TryDeleteCreatedBucketAsync(bucket, cancellationToken);
                    throw;
                }
            }
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            throw new CoreBucketAlreadyExistsException(bucket);
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"create bucket '{bucket}'");
        }
    }

    public async Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.DeleteBucketAsync(
                new DeleteBucketRequest { BucketName = bucket },
                cancellationToken);

            _usageCache.TryRemove(bucket, out _);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Idempotent delete.
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"delete bucket '{bucket}'");
        }
    }

    public async Task<BucketVersioning> GetVersioningAsync(
        string bucket,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetBucketVersioningAsync(
                new GetBucketVersioningRequest { BucketName = bucket },
                cancellationToken);

            var status = response.VersioningConfig?.Status?.Value;

            return new BucketVersioning(
                Enabled: string.Equals(status, VersionStatus.Enabled.Value, StringComparison.OrdinalIgnoreCase),
                Suspended: string.Equals(status, VersionStatus.Suspended.Value, StringComparison.OrdinalIgnoreCase));
        }
        catch (AmazonS3Exception exception) when (IsNotSupported(exception))
        {
            LogCapabilityUnavailable(_logger, exception, "versioning", bucket);
            return new BucketVersioning(false, false);
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"read versioning of bucket '{bucket}'");
        }
    }

    public async Task SetVersioningAsync(
        string bucket,
        bool enabled,
        CancellationToken cancellationToken)
    {
        try
        {
            await _s3.PutBucketVersioningAsync(
                new PutBucketVersioningRequest
                {
                    BucketName = bucket,
                    VersioningConfig = new S3BucketVersioningConfig
                    {
                        Status = enabled ? VersionStatus.Enabled : VersionStatus.Suspended,
                    },
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsNotSupported(exception))
        {
            throw new StorageCapabilityNotSupportedException("bucket versioning");
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"update versioning of bucket '{bucket}'");
        }
    }

    public async Task<BucketSecurity> GetSecurityAsync(
        string bucket,
        CancellationToken cancellationToken)
    {
        return new BucketSecurity(
            await GetObjectLockEnabledAsync(bucket, cancellationToken),
            await GetEncryptionAsync(bucket, cancellationToken));
    }

    public async Task<string?> GetBucketPolicyAsync(
        string bucket,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetBucketPolicyAsync(
                new GetBucketPolicyRequest { BucketName = bucket },
                cancellationToken);

            return response.Policy;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception exception) when (IsNotSupported(exception))
        {
            LogCapabilityUnavailable(_logger, exception, "bucket policy", bucket);
            return null;
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"read policy of bucket '{bucket}'");
        }
    }

    public async Task SetBucketPolicyAsync(
        string bucket,
        string? policy,
        CancellationToken cancellationToken)
    {
        try
        {
            if (policy is null)
            {
                await _s3.DeleteBucketPolicyAsync(
                    new DeleteBucketPolicyRequest { BucketName = bucket },
                    cancellationToken);
            }
            else
            {
                await _s3.PutBucketPolicyAsync(
                    new PutBucketPolicyRequest { BucketName = bucket, Policy = policy },
                    cancellationToken);
            }
        }
        catch (AmazonS3Exception exception) when (IsNotSupported(exception))
        {
            throw new StorageCapabilityNotSupportedException("bucket policy");
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"update policy of bucket '{bucket}'");
        }
    }

    public async Task<IReadOnlyList<CoreLifecycleRule>> GetLifecycleAsync(
        string bucket,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetLifecycleConfigurationAsync(
                new GetLifecycleConfigurationRequest { BucketName = bucket },
                cancellationToken);

            return (response.Configuration?.Rules ?? [])
                .Select(rule => new CoreLifecycleRule(
                    rule.Id ?? string.Empty,
                    rule.Filter?.Prefix,
                    Enabled: !string.Equals(rule.Status?.Value, LifecycleRuleStatus.Disabled.Value, StringComparison.OrdinalIgnoreCase),
                    rule.Expiration?.Days,
                    rule.NoncurrentVersionExpiration?.NoncurrentDays,
                    AbortIncompleteMultipartUpload: rule.AbortIncompleteMultipartUpload is not null))
                .ToArray();
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound || IsNotSupported(exception))
        {
            if (IsNotSupported(exception))
            {
                LogCapabilityUnavailable(_logger, exception, "lifecycle rules", bucket);
            }

            return [];
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"read lifecycle of bucket '{bucket}'");
        }
    }

    public async Task SetLifecycleAsync(
        string bucket,
        IReadOnlyList<CoreLifecycleRule> rules,
        CancellationToken cancellationToken)
    {
        try
        {
            if (rules.Count == 0)
            {
                await _s3.DeleteLifecycleConfigurationAsync(
                    new DeleteLifecycleConfigurationRequest { BucketName = bucket },
                    cancellationToken);

                return;
            }

            await _s3.PutLifecycleConfigurationAsync(
                new PutLifecycleConfigurationRequest
                {
                    BucketName = bucket,
                    Configuration = new LifecycleConfiguration
                    {
                        Rules = rules
                            .Select(rule => new Amazon.S3.Model.LifecycleRule
                            {
                                Id = rule.Id,
                                Filter = new LifecycleFilter
                                {
                                    Prefix = rule.Prefix,
                                },
                                Status = rule.Enabled
                                    ? LifecycleRuleStatus.Enabled
                                    : LifecycleRuleStatus.Disabled,
                                Expiration = rule.ExpireAfterDays is { } expireDays
                                    ? new LifecycleRuleExpiration { Days = expireDays }
                                    : null,
                                NoncurrentVersionExpiration = rule.NoncurrentVersionExpireAfterDays is { } noncurrentDays
                                    ? new LifecycleRuleNoncurrentVersionExpiration { NoncurrentDays = noncurrentDays }
                                    : null,
                                AbortIncompleteMultipartUpload = rule.AbortIncompleteMultipartUpload
                                    ? new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = 7 }
                                    : null,
                            })
                            .ToList(),
                    },
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsNotSupported(exception))
        {
            throw new StorageCapabilityNotSupportedException("lifecycle rules");
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"update lifecycle of bucket '{bucket}'");
        }
    }

    public async Task<ObjectListing> ListObjectsAsync(
        string bucket,
        string? prefix,
        string delimiter,
        int maxKeys,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = prefix,
                    Delimiter = delimiter,
                    MaxKeys = maxKeys,
                    ContinuationToken = continuationToken,
                },
                cancellationToken);

            return new ObjectListing(
                (response.CommonPrefixes ?? []).ToArray(),
                (response.S3Objects ?? [])
                    .Select(item => new ObjectInfo(
                        item.Key,
                        item.Size ?? 0,
                        item.ETag,
                        null,
                        item.LastModified is null
                            ? null
                            : new DateTimeOffset(item.LastModified.Value.ToUniversalTime(), TimeSpan.Zero),
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)))
                    .ToArray(),
                response.NextContinuationToken);
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"list objects in bucket '{bucket}'");
        }
    }

    public async Task<ObjectInfo?> HeadObjectAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = key,
                    VersionId = versionId,
                },
                cancellationToken);

            return new ObjectInfo(
                key,
                response.ContentLength,
                response.ETag,
                NormalizeVersionId(response.VersionId),
                response.LastModified is null
                    ? null
                    : new DateTimeOffset(response.LastModified.Value.ToUniversalTime(), TimeSpan.Zero),
                ToDictionary(response.Metadata),
                response.ContentType);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"read object '{key}' in bucket '{bucket}'");
        }
    }

    public async Task<Stream?> OpenReadAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    VersionId = versionId,
                },
                cancellationToken);

            return new S3ResponseStream(response);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"download object '{key}' from bucket '{bucket}'");
        }
    }

    public async Task<ObjectInfo> PutObjectAsync(
        string bucket,
        string key,
        string contentType,
        long length,
        Stream content,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
                UseChunkEncoding = false,
            };

            AddMetadata(request.Metadata, metadata);

            var response = await _s3.PutObjectAsync(request, cancellationToken);

            _usageCache.TryRemove(bucket, out _);

            return new ObjectInfo(
                key,
                length,
                response.ETag,
                response.VersionId,
                DateTimeOffset.UtcNow,
                metadata is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                contentType);
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"upload object '{key}' to bucket '{bucket}'");
        }
    }

    public async Task DeleteObjectAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _s3.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    VersionId = versionId,
                },
                cancellationToken);

            _usageCache.TryRemove(bucket, out _);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Deleting a missing object is idempotent.
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"delete object '{key}' from bucket '{bucket}'");
        }
    }

    public async Task<IReadOnlyList<ObjectVersion>> ListVersionsAsync(
        string bucket,
        string? prefix,
        string? key,
        CancellationToken cancellationToken)
    {
        try
        {
            var versions = new List<ObjectVersion>();
            string? keyMarker = null;
            string? versionIdMarker = null;

            do
            {
                var response = await _s3.ListVersionsAsync(
                    new ListVersionsRequest
                    {
                        BucketName = bucket,
                        Prefix = prefix,
                        KeyMarker = keyMarker,
                        VersionIdMarker = versionIdMarker,
                    },
                    cancellationToken);

                foreach (var version in response.Versions ?? [])
                {
                    if (key is not null && !string.Equals(version.Key, key, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    versions.Add(new ObjectVersion(
                        NormalizeVersionId(version.VersionId),
                        version.IsLatest ?? false,
                        version.IsDeleteMarker ?? false,
                        version.Size ?? 0,
                        version.ETag,
                        version.LastModified is null
                            ? null
                            : new DateTimeOffset(version.LastModified.Value.ToUniversalTime(), TimeSpan.Zero),
                        null));
                }

                keyMarker = response.NextKeyMarker;
                versionIdMarker = response.NextVersionIdMarker;
            }
            while (!string.IsNullOrEmpty(keyMarker));

            return versions;
        }
        catch (AmazonS3Exception exception) when (IsNotSupported(exception))
        {
            throw new StorageCapabilityNotSupportedException("object versions");
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"list versions in bucket '{bucket}'");
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetTagsAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetObjectTaggingAsync(
                new GetObjectTaggingRequest
                {
                    BucketName = bucket,
                    Key = key,
                    VersionId = versionId,
                },
                cancellationToken);

            return (response.Tagging ?? [])
                .Where(tag => tag.Key is not null)
                .ToDictionary(tag => tag.Key!, tag => tag.Value ?? string.Empty, StringComparer.Ordinal);
        }
        catch (AmazonS3Exception exception) when (IsNotSupported(exception))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"read tags of object '{key}' in bucket '{bucket}'");
        }
    }

    public async Task SetTagsAsync(
        string bucket,
        string key,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken)
    {
        try
        {
            await _s3.PutObjectTaggingAsync(
                new PutObjectTaggingRequest
                {
                    BucketName = bucket,
                    Key = key,
                    Tagging = new Tagging
                    {
                        TagSet = tags
                            .Select(pair => new Tag { Key = pair.Key, Value = pair.Value })
                            .ToList(),
                    },
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsNotSupported(exception))
        {
            throw new StorageCapabilityNotSupportedException("object tags");
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"update tags of object '{key}' in bucket '{bucket}'");
        }
    }

    public string CreatePresignedGetUrl(
        string bucket,
        string key,
        string? versionId,
        DateTimeOffset expiresAt)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId,
            Verb = HttpVerb.GET,
            Expires = expiresAt.UtcDateTime,
            Protocol = _s3Options.ServiceUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase)
                ? Amazon.S3.Protocol.HTTPS
                : Amazon.S3.Protocol.HTTP,
        };

        return _s3.GetPreSignedURL(request);
    }

    public async Task<BucketUsageStats> GetUsageAsync(
        string bucket,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!refresh &&
            _usageCache.TryGetValue(bucket, out var cached) &&
            cached.ExpiresAt > now)
        {
            return cached.Stats;
        }

        long objectCount = 0;
        long usedBytes = 0;
        string? continuationToken = null;

        try
        {
            do
            {
                var response = await _s3.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = bucket,
                        ContinuationToken = continuationToken,
                    },
                    cancellationToken);

                foreach (var item in response.S3Objects ?? [])
                {
                    objectCount++;
                    usedBytes += item.Size ?? 0;
                }

                continuationToken = response.IsTruncated.GetValueOrDefault()
                    ? response.NextContinuationToken
                    : null;
            }
            while (continuationToken is not null);
        }
        catch (AmazonS3Exception exception)
        {
            throw ToStorageException(exception, $"calculate usage of bucket '{bucket}'");
        }

        var stats = new BucketUsageStats(bucket, objectCount, usedBytes, now);
        _usageCache[bucket] = new UsageCacheEntry(
            stats,
            now.AddSeconds(Math.Max(1, _adminOptions.UsageCacheSeconds)));

        return stats;
    }

    private async Task<bool> GetObjectLockEnabledAsync(string bucket, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetObjectLockConfigurationAsync(
                new GetObjectLockConfigurationRequest { BucketName = bucket },
                cancellationToken);

            return string.Equals(
                response.ObjectLockConfiguration?.ObjectLockEnabled?.Value,
                ObjectLockEnabled.Enabled.Value,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound || IsNotSupported(exception))
        {
            return false;
        }
        catch (AmazonS3Exception exception)
        {
            LogCapabilityUnavailable(_logger, exception, "object lock", bucket);
            return false;
        }
    }

    private async Task<string?> GetEncryptionAsync(string bucket, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _s3.GetBucketEncryptionAsync(
                new GetBucketEncryptionRequest { BucketName = bucket },
                cancellationToken);

            var rule = response.ServerSideEncryptionConfiguration?
                .ServerSideEncryptionRules?
                .FirstOrDefault();

            return rule?.ServerSideEncryptionByDefault?.ServerSideEncryptionAlgorithm?.Value;
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound || IsNotSupported(exception))
        {
            return null;
        }
        catch (AmazonS3Exception exception)
        {
            LogCapabilityUnavailable(_logger, exception, "bucket encryption", bucket);
            return null;
        }
    }

    private async Task TryDeleteCreatedBucketAsync(string bucket, CancellationToken cancellationToken)
    {
        try
        {
            await DeleteBucketAsync(bucket, cancellationToken);
        }
        catch (Exception exception)
        {
            LogRollbackFailed(_logger, exception, bucket);
        }
    }

    private static string? NormalizeVersionId(string? versionId)
    {
        return string.IsNullOrWhiteSpace(versionId) || versionId == "null" ? null : versionId;
    }

    private static bool IsNotSupported(AmazonS3Exception exception)
    {
        return exception.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed ||
               exception.ErrorCode is "NotImplemented" or "MethodNotAllowed";
    }

    private static ObjectStorageException ToStorageException(AmazonS3Exception exception, string operation)
    {
        return new ObjectStorageException($"S3 failed to {operation}.", exception);
    }

    private static void AddMetadata(MetadataCollection target, IReadOnlyDictionary<string, string>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            target[key] = value;
        }
    }

    private static Dictionary<string, string> ToDictionary(MetadataCollection metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawKey in metadata.Keys)
        {
            var key = rawKey.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                ? rawKey["x-amz-meta-".Length..]
                : rawKey;

            result[key] = metadata[rawKey];
        }

        return result;
    }

    private sealed record UsageCacheEntry(BucketUsageStats Stats, DateTimeOffset ExpiresAt);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message = "S3 provider does not support {Capability} for bucket {Bucket}; using degraded behaviour.")]
    private static partial void LogCapabilityUnavailable(ILogger logger, Exception exception, string capability, string bucket);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Warning, Message = "Rollback of partially created bucket {Bucket} failed.")]
    private static partial void LogRollbackFailed(ILogger logger, Exception exception, string bucket);

    private sealed class S3ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            response.Dispose();
            await base.DisposeAsync();
        }
    }
}
