namespace Reon.Core.Enums;

/// <summary>
/// Возможность S3-совместимого провайдера, которую административный фасад
/// может показать или скрыть в интерфейсе. Значения принадлежат провайдеру,
/// а не отдельному бакету.
/// </summary>
public enum StorageCapability
{
    Versioning = 0,
    ObjectTags = 1,
    Lifecycle = 2,
    BucketPolicy = 3,
    ObjectLock = 4,
}

public static class StorageCapabilityExtensions
{
    /// <summary>Канонический ключ для API, конфигурации и таблицы возможностей.</summary>
    public static string ToApiValue(this StorageCapability capability)
    {
        return capability switch
        {
            StorageCapability.Versioning => "versioning",
            StorageCapability.ObjectTags => "object-tags",
            StorageCapability.Lifecycle => "lifecycle",
            StorageCapability.BucketPolicy => "bucket-policy",
            StorageCapability.ObjectLock => "object-lock",
            _ => capability.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>Человекочитаемое название для ProblemDetails.</summary>
    public static string ToDisplayName(this StorageCapability capability)
    {
        return capability switch
        {
            StorageCapability.Versioning => "bucket versioning",
            StorageCapability.ObjectTags => "object tags",
            StorageCapability.Lifecycle => "lifecycle rules",
            StorageCapability.BucketPolicy => "bucket policy",
            StorageCapability.ObjectLock => "object lock",
            _ => capability.ToString(),
        };
    }
}
