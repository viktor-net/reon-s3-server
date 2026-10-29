namespace Reon.Core.Interfaces;

/// <summary>
/// Administrative operations over the object store that span multiple buckets.
/// The file API deliberately stays on a single default bucket; this interface
/// is only used by the administrative contour.
/// </summary>
public interface IObjectStorageAdmin
{
    Task<IReadOnlyList<StorageBucket>> ListBucketsAsync(CancellationToken cancellationToken);

    Task<StorageBucket?> GetBucketAsync(string bucket, CancellationToken cancellationToken);

    Task CreateBucketAsync(
        string bucket,
        bool versioningEnabled,
        bool objectLockEnabled,
        CancellationToken cancellationToken);

    /// <summary>Deletes a bucket. Used for rollback and administrative cleanup.</summary>
    Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken);

    Task<BucketVersioning> GetVersioningAsync(string bucket, CancellationToken cancellationToken);

    Task SetVersioningAsync(string bucket, bool enabled, CancellationToken cancellationToken);

    Task<BucketSecurity> GetSecurityAsync(string bucket, CancellationToken cancellationToken);

    Task<string?> GetBucketPolicyAsync(string bucket, CancellationToken cancellationToken);

    Task SetBucketPolicyAsync(string bucket, string? policy, CancellationToken cancellationToken);

    Task<IReadOnlyList<LifecycleRule>> GetLifecycleAsync(string bucket, CancellationToken cancellationToken);

    Task SetLifecycleAsync(
        string bucket,
        IReadOnlyList<LifecycleRule> rules,
        CancellationToken cancellationToken);

    Task<ObjectListing> ListObjectsAsync(
        string bucket,
        string? prefix,
        string delimiter,
        int maxKeys,
        string? continuationToken,
        CancellationToken cancellationToken);

    Task<ObjectInfo?> HeadObjectAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken);

    Task<Stream?> OpenReadAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken);

    Task<ObjectInfo> PutObjectAsync(
        string bucket,
        string key,
        string contentType,
        long length,
        Stream content,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken);

    Task DeleteObjectAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ObjectVersion>> ListVersionsAsync(
        string bucket,
        string? prefix,
        string? key,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetTagsAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken);

    Task SetTagsAsync(
        string bucket,
        string key,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken);

    string CreatePresignedGetUrl(
        string bucket,
        string key,
        string? versionId,
        DateTimeOffset expiresAt);

    /// <summary>Recomputes usage from the object store, with a short-lived cache.</summary>
    Task<BucketUsageStats> GetUsageAsync(string bucket, bool refresh, CancellationToken cancellationToken);
}
