namespace Reon.Core.Exceptions;

public sealed class MultipartUploadExpiredException : Exception
{
    public MultipartUploadExpiredException(Guid uploadId)
        : base($"Multipart upload '{uploadId}' has expired.")
    {
        UploadId = uploadId;
    }

    public Guid UploadId { get; }
}
