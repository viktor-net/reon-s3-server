namespace Reon.Core.Entities;

/// <summary>
/// Метаданные одного файла. Сущность владеет правилами жизненного цикла;
/// API и слой хранения только вызывают методы переходов.
/// </summary>
public sealed class FileMetadata
{
    private FileMetadata()
    {
        // Требуется для EF Core.
    }

    public Guid Id { get; private set; }

    public string Bucket { get; private set; } = string.Empty;

    public string ObjectKey { get; private set; } = string.Empty;

    public string OriginalName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = "application/octet-stream";

    public long Size { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public string? ETag { get; private set; }

    public string? S3VersionId { get; private set; }

    public string Owner { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public JsonDocument? Tags { get; private set; }

    public FileStatus Status { get; private set; }

    public int Version { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public static FileMetadata CreatePending(
        Guid id,
        string bucket,
        string objectKey,
        string originalName,
        string contentType,
        long size,
        string sha256,
        string owner,
        string? description,
        JsonDocument? tags,
        int version,
        DateTimeOffset now)
    {
        return new FileMetadata
        {
            Id = id,
            Bucket = bucket,
            ObjectKey = objectKey,
            OriginalName = originalName,
            ContentType = contentType,
            Size = size,
            Sha256 = sha256,
            Owner = owner,
            Description = description,
            Tags = tags,
            Status = FileStatus.Pending,
            Version = version,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void MarkReady(string? etag, string? s3VersionId, DateTimeOffset now)
    {
        if (Status != FileStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot mark file {Id} as ready from status {Status}.");
        }

        Status = FileStatus.Ready;
        ETag = etag;
        S3VersionId = s3VersionId;
        FailureReason = null;
        CompletedAt = now;
        UpdatedAt = now;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        if (Status == FileStatus.Deleted)
        {
            return;
        }

        Status = FileStatus.Failed;
        FailureReason = reason;
        UpdatedAt = now;
    }

    public void BeginDelete(DateTimeOffset now)
    {
        if (Status is FileStatus.Deleting or FileStatus.Deleted)
        {
            return;
        }

        Status = FileStatus.Deleting;
        UpdatedAt = now;
    }

    public void MarkDeleted(DateTimeOffset now)
    {
        Status = FileStatus.Deleted;
        DeletedAt = now;
        UpdatedAt = now;
    }

    public void MarkMissing(string reason, DateTimeOffset now)
    {
        if (Status == FileStatus.Deleted)
        {
            return;
        }

        Status = FileStatus.Missing;
        FailureReason = reason;
        UpdatedAt = now;
    }

    public void MarkCorrupted(string reason, DateTimeOffset now)
    {
        if (Status == FileStatus.Deleted)
        {
            return;
        }

        Status = FileStatus.Corrupted;
        FailureReason = reason;
        UpdatedAt = now;
    }

    public void UpdateMetadata(
        string originalName,
        string? description,
        JsonDocument? tags,
        DateTimeOffset now)
    {
        if (Status == FileStatus.Deleted)
        {
            throw new InvalidOperationException($"Cannot update deleted file {Id}.");
        }

        OriginalName = originalName;
        Description = description;
        Tags = tags;
        UpdatedAt = now;
    }
}
