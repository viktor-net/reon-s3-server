using Reon.Core.Enums;

namespace Reon.Core.Entities;

public sealed class MultipartUpload
{
    private MultipartUpload()
    {
        // Требуется для EF Core.
    }

    public Guid Id { get; private set; }

    public Guid FileId { get; private set; }

    /// <summary>Непрозрачный идентификатор multipart-загрузки S3. Клиентам не показывается.</summary>
    public string StorageUploadId { get; private set; } = string.Empty;

    public string Bucket { get; private set; } = string.Empty;

    public string ObjectKey { get; private set; } = string.Empty;

    public string OriginalName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = "application/octet-stream";

    public string? Description { get; private set; }

    public JsonDocument? Tags { get; private set; }

    public string Owner { get; private set; } = string.Empty;

    public MultipartUploadStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? FailureReason { get; private set; }

    public static MultipartUpload Create(
        Guid id,
        Guid fileId,
        string storageUploadId,
        string bucket,
        string objectKey,
        string originalName,
        string contentType,
        string? description,
        JsonDocument? tags,
        string owner,
        DateTimeOffset now,
        DateTimeOffset expiresAt)
    {
        return new MultipartUpload
        {
            Id = id,
            FileId = fileId,
            StorageUploadId = storageUploadId,
            Bucket = bucket,
            ObjectKey = objectKey,
            OriginalName = originalName,
            ContentType = contentType,
            Description = description,
            Tags = tags,
            Owner = owner,
            Status = MultipartUploadStatus.Initiated,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = expiresAt,
        };
    }

    public bool IsActive =>
        Status is MultipartUploadStatus.Initiated or MultipartUploadStatus.InProgress;

    public void MarkPartUploaded(DateTimeOffset now)
    {
        if (!IsActive)
        {
            return;
        }

        Status = MultipartUploadStatus.InProgress;
        UpdatedAt = now;
    }

    public void BeginComplete(DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException($"Cannot complete upload {Id} from status {Status}.");
        }

        Status = MultipartUploadStatus.Completing;
        UpdatedAt = now;
    }

    public void MarkCompleted(DateTimeOffset now)
    {
        Status = MultipartUploadStatus.Completed;
        CompletedAt = now;
        FailureReason = null;
        UpdatedAt = now;
    }

    public void MarkAborted(DateTimeOffset now)
    {
        if (Status == MultipartUploadStatus.Completed)
        {
            throw new InvalidOperationException($"Cannot abort completed upload {Id}.");
        }

        Status = MultipartUploadStatus.Aborted;
        UpdatedAt = now;
    }

    public void MarkExpired(DateTimeOffset now)
    {
        if (Status == MultipartUploadStatus.Completed)
        {
            return;
        }

        Status = MultipartUploadStatus.Expired;
        FailureReason = "The multipart upload expired.";
        UpdatedAt = now;
    }

    public void MarkFailed(string reason, DateTimeOffset now)
    {
        Status = MultipartUploadStatus.Failed;
        FailureReason = reason;
        UpdatedAt = now;
    }
}
