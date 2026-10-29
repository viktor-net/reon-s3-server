namespace Reon.Core.Exceptions;

/// <summary>
/// Возникает, когда S3-совместимый провайдер не реализует
/// административную возможность (например, bucket policy в Swift s3api).
/// </summary>
public sealed class StorageCapabilityNotSupportedException : Exception
{
    public StorageCapabilityNotSupportedException(string capability)
        : base($"The object storage provider does not support {capability}.")
    {
        Capability = capability;
    }

    public string Capability { get; }
}
