using Reon.Core.Enums;

namespace Reon.Core.Models;

public sealed record StorageBucket(
    string Name,
    DateTimeOffset? CreatedAt);

public sealed record BucketVersioning(
    bool Enabled,
    bool Suspended);

public sealed record BucketSecurity(
    bool ObjectLockEnabled,
    string? Encryption);

public sealed record LifecycleRule(
    string Id,
    string? Prefix,
    bool Enabled,
    int? ExpireAfterDays,
    int? NoncurrentVersionExpireAfterDays,
    bool AbortIncompleteMultipartUpload);

public sealed record BucketAccess(
    BucketAccessMode Mode,
    string Policy);

/// <summary>Raw object listing page: prefixes (folders) and objects.</summary>
public sealed record ObjectListing(
    IReadOnlyList<string> Prefixes,
    IReadOnlyList<ObjectInfo> Objects,
    string? NextContinuationToken);

public sealed record ObjectVersion(
    string? VersionId,
    bool IsLatest,
    bool IsDeleteMarker,
    long Size,
    string? ETag,
    DateTimeOffset? LastModified,
    string? ContentType);

public sealed record BucketUsageStats(
    string Bucket,
    long ObjectCount,
    long UsedBytes,
    DateTimeOffset CalculatedAt);

public sealed record BucketUsage(
    string Bucket,
    long ObjectCount,
    long UsedBytes,
    long? QuotaBytes,
    DateTimeOffset CalculatedAt)
{
    public double? UsagePercent => QuotaBytes is > 0
        ? Math.Round((double)UsedBytes / QuotaBytes.Value * 100, 2)
        : null;

    public string Status => QuotaBytes is not > 0
        ? "NoQuota"
        : UsagePercent >= 95
            ? "Critical"
            : UsagePercent >= 80
                ? "Warning"
                : "Ok";
}

public sealed record BucketSummary(
    string Name,
    DateTimeOffset? CreatedAt,
    BucketVersioning Versioning,
    BucketSecurity Security,
    BucketAccessMode AccessMode,
    BucketUsage Usage);

public sealed record BucketListQuery(
    string? Search = null,
    int Page = 1,
    int PageSize = 50,
    string? Sort = "name");

public sealed record BucketListResult(
    IReadOnlyList<BucketSummary> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record CreateBucketCommand(
    string Name,
    bool VersioningEnabled,
    bool ObjectLockEnabled,
    long? QuotaBytes);

public sealed record UpdateBucketCommand(
    bool? VersioningEnabled,
    long? QuotaBytes,
    bool RemoveQuota);

public sealed record StorageOverview(
    int BucketCount,
    long ObjectCount,
    long TotalBytes,
    int PublicBucketCount,
    IReadOnlyList<BucketSummary> Buckets,
    DateTimeOffset GeneratedAt);

public sealed record ObjectListQuery(
    string Bucket,
    string? Prefix = null,
    string Delimiter = "/",
    string? Search = null,
    int MaxKeys = 100,
    string? ContinuationToken = null,
    bool IncludeContentType = false);

public sealed record ObjectContent(
    Stream Stream,
    string ContentType,
    long Length,
    string? ETag,
    string? VersionId);

public sealed record UploadObjectCommand(
    string Bucket,
    string Key,
    string ContentType,
    long Size,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyDictionary<string, string>? Tags = null);

/// <summary>
/// One file of a batch upload. The stream factory keeps the facade free of
/// ASP.NET types: the API layer opens the form file on demand.
/// </summary>
public sealed record BatchUploadEntry(
    string Key,
    string ContentType,
    long Size,
    Func<CancellationToken, ValueTask<Stream>> OpenStream);

public sealed record PresignedUrl(
    string Url,
    DateTimeOffset ExpiresAt,
    string Method);

public sealed record AuditQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Actor = null,
    string? Action = null,
    string? Resource = null,
    AuditResult? Result = null,
    int Page = 1,
    int PageSize = 50);

public sealed record AuditPage(
    IReadOnlyList<AuditEntry> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>Envelope pushed to administrative WebSocket subscribers.</summary>
public sealed record StorageEvent(
    string Type,
    DateTimeOffset OccurredAt,
    object Data,
    string? Bucket = null)
{
    public const string AuditAppended = "audit.appended";
    public const string BucketChanged = "bucket.changed";
    public const string ObjectChanged = "object.changed";
    public const string UsageUpdated = "usage.updated";
    public const string Heartbeat = "heartbeat";
}
