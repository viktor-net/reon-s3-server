using Reon.Core.Enums;

namespace Reon.Core.Entities;

/// <summary>
/// Административные настройки бакета, которыми владеет этот сервис.
/// Сам бакет живёт в объектном хранилище; здесь хранятся только настройки,
/// которые S3 API не может переносимо представить: квота и режим анонимного
/// доступа. Версионирование, lifecycle и безопасность читаются из хранилища.
/// </summary>
public sealed class BucketSettings
{
    private BucketSettings()
    {
        // Требуется для EF Core.
    }

    public string Bucket { get; private set; } = string.Empty;

    /// <summary>Квота в байтах (best-effort). Null означает отсутствие квоты.</summary>
    public long? QuotaBytes { get; private set; }

    public BucketAccessMode AccessMode { get; private set; }

    /// <summary>Защита бакета от удаления включена.</summary>
    public bool ObjectLockEnabled { get; private set; }

    /// <summary>Источник защиты: настоящий S3 Object Lock или сервисная блокировка.</summary>
    public ObjectLockMode ObjectLockMode { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static BucketSettings Create(
        string bucket,
        long? quotaBytes,
        DateTimeOffset now,
        ObjectLockMode objectLockMode = ObjectLockMode.None)
    {
        return new BucketSettings
        {
            Bucket = bucket,
            QuotaBytes = quotaBytes,
            AccessMode = BucketAccessMode.Private,
            ObjectLockEnabled = objectLockMode != ObjectLockMode.None,
            ObjectLockMode = objectLockMode,
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

    public void SetObjectLock(ObjectLockMode mode, DateTimeOffset now)
    {
        ObjectLockMode = mode;
        ObjectLockEnabled = mode != ObjectLockMode.None;
        UpdatedAt = now;
    }
}
