namespace Reon.Api.Contracts;

internal static class AdminMappings
{
    public static AdminOverviewResponse ToResponse(this StorageOverview overview)
    {
        return new AdminOverviewResponse(
            overview.BucketCount,
            overview.ObjectCount,
            overview.TotalBytes,
            overview.PublicBucketCount,
            overview.Buckets.Select(ToResponse).ToArray(),
            overview.GeneratedAt);
    }

    public static BucketSummaryResponse ToResponse(this BucketSummary summary)
    {
        return new BucketSummaryResponse(
            summary.Name,
            summary.CreatedAt,
            new BucketVersioningResponse(summary.Versioning.Enabled, summary.Versioning.Suspended),
            new BucketSecurityResponse(summary.Security.ObjectLockEnabled, summary.Security.Encryption),
            summary.AccessMode.ToString(),
            summary.Usage.ToResponse());
    }

    public static BucketUsageResponse ToResponse(this BucketUsage usage)
    {
        return new BucketUsageResponse(
            usage.Bucket,
            usage.ObjectCount,
            usage.UsedBytes,
            usage.QuotaBytes,
            usage.UsagePercent,
            usage.Status,
            usage.CalculatedAt);
    }

    public static BucketListResponse ToResponse(this BucketListResult result)
    {
        return new BucketListResponse(
            result.Items.Select(ToResponse).ToArray(),
            result.Total,
            result.Page,
            result.PageSize);
    }

    public static LifecycleRuleResponse ToResponse(this LifecycleRule rule)
    {
        return new LifecycleRuleResponse(
            rule.Id,
            rule.Prefix,
            rule.Enabled,
            rule.ExpireAfterDays,
            rule.NoncurrentVersionExpireAfterDays,
            rule.AbortIncompleteMultipartUpload);
    }

    public static ObjectItemResponse ToResponse(this ObjectInfo info, string? versionId = null)
    {
        return new ObjectItemResponse(
            info.ObjectKey,
            info.Size,
            info.ETag,
            info.ContentType,
            info.LastModified,
            versionId ?? info.VersionId);
    }

    public static ObjectVersionResponse ToResponse(this ObjectVersion version)
    {
        return new ObjectVersionResponse(
            version.VersionId,
            version.IsLatest,
            version.IsDeleteMarker,
            version.Size,
            version.ETag,
            version.LastModified,
            version.ContentType);
    }

    public static AuditEntryResponse ToResponse(this AuditEntry entry)
    {
        return new AuditEntryResponse(
            entry.Id,
            entry.OccurredAt,
            entry.Actor,
            entry.Action,
            entry.Resource,
            entry.Result.ToString(),
            entry.StatusCode,
            entry.Details?.RootElement.Clone());
    }

    public static AuditListResponse ToResponse(this AuditPage page)
    {
        return new AuditListResponse(
            page.Items.Select(ToResponse).ToArray(),
            page.Total,
            page.Page,
            page.PageSize);
    }
}
