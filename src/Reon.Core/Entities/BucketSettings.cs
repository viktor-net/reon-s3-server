using Reon.Core.Enums;

namespace Reon.Core.Entities;

/// <summary>
/// Administrative settings owned by this service for a storage bucket.
/// The bucket itself lives in the object store; only settings that the S3 API
/// cannot represent in a portable way (quota, anonymous access mode) are kept
/// here. Versioning, lifecycle and security are read from the object store.
/// </summary>
public sealed class BucketSettings
{
    private BucketSettings()
    {
        // Required by EF Core.
    }

    public string Bucket { get; private set; } = string.Empty;

    /// <summary>Best-effort quota in bytes. Null means no quota.</summary>
    public long? QuotaBytes { get; private set; }

    public BucketAccessMode AccessMode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static BucketSettings Create(
        string bucket,
        long? quotaBytes,
        DateTimeOffset now)
    {
        return new BucketSettings
        {
            Bucket = bucket,
            QuotaBytes = quotaBytes,
            AccessMode = BucketAccessMode.Private,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void SetQuota(long? quotaBytes, DateTimeOffset now)
    {
        QuotaBytes = quotaBytes;
        UpdatedAt = now;
    }

    public void SetAccessMode(BucketAccessMode accessMode, DateTimeOffset now)
    {
        AccessMode = accessMode;
        UpdatedAt = now;
    }
}
