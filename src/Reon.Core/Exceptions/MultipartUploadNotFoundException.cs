namespace Reon.Core.Exceptions;

public sealed class MultipartUploadNotFoundException : Exception
{
    public MultipartUploadNotFoundException(Guid uploadId)
        : base($"Multipart upload '{uploadId}' was not found.")
    {
        UploadId = uploadId;
    }

    public Guid UploadId { get; }
}
