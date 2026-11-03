namespace Reon.Core.Models;

/// <summary>
/// Срез возможностей текущего S3-провайдера. Возможности принадлежат
/// провайдеру, а не отдельному бакету.
/// </summary>
public sealed record StorageCapabilities(
    bool Versioning,
    bool ObjectTags,
    bool Lifecycle,
    bool BucketPolicy,
    bool ObjectLock)
{
    public bool Supports(StorageCapability capability)
    {
        return capability switch
        {
            StorageCapability.Versioning => Versioning,
            StorageCapability.ObjectTags => ObjectTags,
            StorageCapability.Lifecycle => Lifecycle,
            StorageCapability.BucketPolicy => BucketPolicy,
            StorageCapability.ObjectLock => ObjectLock,
            _ => false,
        };
    }
}
