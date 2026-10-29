using Microsoft.AspNetCore.Http;

namespace Reon.Api.Contracts;

public sealed record AdminOverviewResponse(
    int BucketCount,
    long ObjectCount,
    long TotalBytes,
    int PublicBucketCount,
    IReadOnlyList<BucketSummaryResponse> Buckets,
    DateTimeOffset GeneratedAt);

public sealed record BucketSummaryResponse(
    string Name,
    DateTimeOffset? CreatedAt,
    BucketVersioningResponse Versioning,
    BucketSecurityResponse Security,
    string AccessMode,
    BucketUsageResponse Usage);

public sealed record BucketVersioningResponse(
    bool Enabled,
    bool Suspended);

public sealed record BucketSecurityResponse(
    bool ObjectLockEnabled,
    string? Encryption);

public sealed record BucketUsageResponse(
    string Bucket,
    long ObjectCount,
    long UsedBytes,
    long? QuotaBytes,
    double? UsagePercent,
    string Status,
    DateTimeOffset CalculatedAt);

public sealed record BucketListResponse(
    IReadOnlyList<BucketSummaryResponse> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record CreateBucketRequest(
    string Name,
    bool VersioningEnabled = false,
    bool ObjectLockEnabled = false,
    long? QuotaBytes = null);

public sealed record UpdateBucketRequest(
    bool? VersioningEnabled = null,
    long? QuotaBytes = null,
    bool RemoveQuota = false);

public sealed record SetBucketAccessRequest(string Mode);

public sealed record BucketAccessResponse(string Mode, string Policy);

public sealed record LifecycleRuleRequest(
    string Id,
    string? Prefix = null,
    bool Enabled = true,
    int? ExpireAfterDays = null,
    int? NoncurrentVersionExpireAfterDays = null,
    bool AbortIncompleteMultipartUpload = false);

public sealed record LifecycleRuleResponse(
    string Id,
    string? Prefix,
    bool Enabled,
    int? ExpireAfterDays,
    int? NoncurrentVersionExpireAfterDays,
    bool AbortIncompleteMultipartUpload);

public sealed record BucketLifecycleResponse(IReadOnlyList<LifecycleRuleResponse> Rules);

public sealed record ObjectListingResponse(
    IReadOnlyList<string> Prefixes,
    IReadOnlyList<ObjectItemResponse> Items,
    string? NextContinuationToken);

public sealed record ObjectItemResponse(
    string Key,
    long Size,
    string? ETag,
    string? ContentType,
    DateTimeOffset? LastModified,
    string? VersionId = null);

public sealed record ObjectMetadataResponse(
    string Bucket,
    string Key,
    long Size,
    string? ContentType,
    string? ETag,
    string? VersionId,
    DateTimeOffset? LastModified,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyDictionary<string, string> Tags,
    int VersionCount);

public sealed record ObjectVersionsResponse(IReadOnlyList<ObjectVersionResponse> Items);

public sealed record ObjectVersionResponse(
    string? VersionId,
    bool IsLatest,
    bool IsDeleteMarker,
    long Size,
    string? ETag,
    DateTimeOffset? LastModified,
    string? ContentType);

public sealed record SetObjectTagsRequest(Dictionary<string, string> Tags);

public sealed record PresignedUrlRequest(int ExpiresInSeconds = 3600);

public sealed record PresignedUrlResponse(string Url, DateTimeOffset ExpiresAt, string Method);

public sealed record CreateFolderRequest(string Name, string? Prefix = null);

public sealed record BatchUploadResponse(IReadOnlyList<ObjectItemResponse> Items);

public sealed class UploadObjectsRequest
{
    public IFormFileCollection? Files { get; set; }

    public string? Prefix { get; set; }

    /// <summary>JSON-объект с пользовательскими метаданными для каждого загружаемого файла.</summary>
    public string? Metadata { get; set; }

    /// <summary>JSON-объект с тегами для каждого загружаемого файла.</summary>
    public string? Tags { get; set; }
}

public sealed record AuditEntryResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    string Resource,
    string Result,
    int? StatusCode,
    JsonElement? Details);

public sealed record AuditListResponse(
    IReadOnlyList<AuditEntryResponse> Items,
    int Total,
    int Page,
    int PageSize);
