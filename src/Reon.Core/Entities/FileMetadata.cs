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

    /// <summary>Момент, после которого файл считается просроченным. null — бессрочно.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>Запрет на удаление по сроку. Не меняет ExpiresAt, только приостанавливает GC.</summary>
    public bool RetentionHold { get; private set; }

    /// <summary>Когда файл перешёл в Expired. null, если срок ещё не наступил.</summary>
    public DateTimeOffset? ExpiredAt { get; private set; }

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
        DateTimeOffset now,
        DateTimeOffset? expiresAt = null,
        bool retentionHold = false)
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
            ExpiresAt = expiresAt,
            RetentionHold = retentionHold,
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

    /// <summary>
    /// Проверяет, истёк ли срок хранения с учётом запрета на удаление.
    /// Проверка не зависит от статуса: между наступлением ExpiresAt и проходом
    /// сборщика мусора строка ещё может быть Ready.
    /// </summary>
    public bool IsExpired(DateTimeOffset now)
    {
        return !RetentionHold && ExpiresAt is { } expiresAt && expiresAt <= now;
    }

    /// <summary>
    /// Переводит Ready в Expired. Повторный вызов идемпотентен.
    /// Файлы под RetentionHold не просрочиваются.
    /// </summary>
    public void MarkExpired(DateTimeOffset now)
    {
        if (Status is FileStatus.Deleted or FileStatus.Deleting || RetentionHold)
        {
            return;
        }

        if (Status != FileStatus.Ready)
        {
            return;
        }

        if (ExpiresAt is not { } expiresAt || expiresAt > now)
        {
            return;
        }

        Status = FileStatus.Expired;
        ExpiredAt = now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Ставит или снимает запрет на удаление по сроку. Снятие запрета с уже
    /// просроченного файла возвращает его в Ready: байты ещё в S3, и файл
    /// снова доступен до следующего прохода GC.
    /// </summary>
    public void SetRetentionHold(bool hold, DateTimeOffset now)
    {
        if (Status is FileStatus.Deleted or FileStatus.Deleting)
        {
            throw new InvalidOperationException($"Cannot change retention hold for file {Id} in status {Status}.");
        }

        RetentionHold = hold;

        if (hold && Status == FileStatus.Expired)
        {
            Status = FileStatus.Ready;
            ExpiredAt = null;
        }

        UpdatedAt = now;
    }

    /// <summary>
    /// Меняет срок хранения. Продление уже просроченного файла возвращает его
    /// в Ready, если новый срок ещё не наступил.
    /// </summary>
    public void ExtendRetention(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (Status is FileStatus.Deleted or FileStatus.Deleting)
        {
            throw new InvalidOperationException($"Cannot change retention for file {Id} in status {Status}.");
        }

        ExpiresAt = expiresAt;

        if (Status == FileStatus.Expired && (expiresAt is null || expiresAt > now))
        {
            Status = FileStatus.Ready;
            ExpiredAt = null;
        }

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
