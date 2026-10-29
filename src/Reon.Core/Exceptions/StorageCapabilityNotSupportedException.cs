namespace Reon.Core.Exceptions;

/// <summary>
/// Raised when the underlying S3-compatible provider does not implement an
/// administrative capability (for example bucket policy on Swift s3api).
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
