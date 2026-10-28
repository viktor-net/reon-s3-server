namespace Reon.Core.Options;

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    /// <summary>Owner value stored for files while client authentication is disabled.</summary>
    public string DefaultOwner { get; set; } = "system";

    /// <summary>Maximum size of a single uploaded file. Default: 1 GiB.</summary>
    public long MaxFileSizeBytes { get; set; } = 1_073_741_824;

    /// <summary>Files larger than this are uploaded with S3 multipart. Default: 64 MiB.</summary>
    public long MultipartThresholdBytes { get; set; } = 67_108_864;

    /// <summary>Part size for multipart uploads. S3 requires at least 5 MiB. Default: 16 MiB.</summary>
    public int MultipartPartSizeBytes { get; set; } = 16_777_216;

    /// <summary>Every generated object key starts with this prefix.</summary>
    public string KeyPrefix { get; set; } = "files/";

    /// <summary>A Pending row older than this is checked against S3 by reconciliation.</summary>
    public int PendingTimeoutMinutes { get; set; } = 60;

    /// <summary>How often a Deleting row is retried.</summary>
    public int DeletingRetryMinutes { get; set; } = 5;

    /// <summary>Background reconciliation interval.</summary>
    public int ReconciliationIntervalMinutes { get; set; } = 5;

    public bool OrphanSweepEnabled { get; set; } = true;

    public int OrphanSweepIntervalMinutes { get; set; } = 360;

    /// <summary>An object with no metadata row must be older than this before it is removed.</summary>
    public int OrphanGracePeriodHours { get; set; } = 24;

    /// <summary>Soft-deleted rows are purged after this many days. 0 keeps them forever.</summary>
    public int DeletedRetentionDays { get; set; } = 30;

    public int MaxTags { get; set; } = 50;

    public int MaxTagKeyLength { get; set; } = 64;

    public int MaxTagValueLength { get; set; } = 256;

    public int MaxDescriptionLength { get; set; } = 2000;

    public int MaxOriginalNameLength { get; set; } = 512;

    public int MaxContentTypeLength { get; set; } = 255;
}
