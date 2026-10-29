namespace Reon.Core.Interfaces;

public interface IStorageAdminService
{
    Task<StorageOverview> GetOverviewAsync(bool refreshUsage, CancellationToken cancellationToken);

    Task<BucketListResult> ListBucketsAsync(BucketListQuery query, CancellationToken cancellationToken);

    Task<BucketSummary> GetBucketAsync(string bucket, bool refreshUsage, CancellationToken cancellationToken);

    Task<BucketSummary> CreateBucketAsync(
        CreateBucketCommand command,
        string actor,
        CancellationToken cancellationToken);

    Task<BucketSummary> UpdateBucketAsync(
        string bucket,
        UpdateBucketCommand command,
        string actor,
        CancellationToken cancellationToken);

    Task<BucketAccess> GetBucketAccessAsync(string bucket, CancellationToken cancellationToken);

    Task<BucketAccess> SetBucketAccessAsync(
        string bucket,
        BucketAccessMode mode,
        string actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LifecycleRule>> GetLifecycleAsync(string bucket, CancellationToken cancellationToken);

    Task<IReadOnlyList<LifecycleRule>> SetLifecycleAsync(
        string bucket,
        IReadOnlyList<LifecycleRule> rules,
        string actor,
        CancellationToken cancellationToken);

    Task<BucketUsage> GetUsageAsync(string bucket, bool refresh, CancellationToken cancellationToken);

    Task<BucketUsage> RefreshUsageAsync(string bucket, string actor, CancellationToken cancellationToken);

    Task<ObjectListing> ListObjectsAsync(ObjectListQuery query, CancellationToken cancellationToken);

    Task<ObjectInfo> GetObjectInfoAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken);

    Task<ObjectContent> OpenObjectAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ObjectVersion>> ListVersionsAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken);

    Task DeleteVersionAsync(
        string bucket,
        string key,
        string versionId,
        string actor,
        CancellationToken cancellationToken);

    Task<ObjectInfo> UploadObjectAsync(
        UploadObjectCommand command,
        Stream content,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Uploads several objects as one logical operation: when any entry fails,
    /// the objects already written by this call are removed again.
    /// </summary>
    Task<IReadOnlyList<ObjectInfo>> UploadBatchAsync(
        string bucket,
        IReadOnlyList<BatchUploadEntry> entries,
        IReadOnlyDictionary<string, string>? metadata,
        IReadOnlyDictionary<string, string>? tags,
        string actor,
        CancellationToken cancellationToken);

    Task DeleteObjectAsync(
        string bucket,
        string key,
        string? versionId,
        string actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetTagsAsync(
        string bucket,
        string key,
        string? versionId,
        CancellationToken cancellationToken);

    Task SetTagsAsync(
        string bucket,
        string key,
        string? versionId,
        IReadOnlyDictionary<string, string> tags,
        string actor,
        CancellationToken cancellationToken);

    Task<PresignedUrl> CreatePresignedUrlAsync(
        string bucket,
        string key,
        string? versionId,
        TimeSpan expiresIn,
        string actor,
        CancellationToken cancellationToken);

    Task CreateFolderAsync(
        string bucket,
        string? prefix,
        string name,
        string actor,
        CancellationToken cancellationToken);

    Task<AuditPage> ListAuditAsync(AuditQuery query, CancellationToken cancellationToken);
}
