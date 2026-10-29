using Reon.Core.Enums;

namespace Reon.Core.Exceptions;

public sealed class MultipartUploadNotActiveException : Exception
{
    public MultipartUploadNotActiveException(Guid uploadId, MultipartUploadStatus status)
        : base($"Multipart upload '{uploadId}' is not active (status: {status}).")
    {
        UploadId = uploadId;
        Status = status;
    }

    public Guid UploadId { get; }

    public MultipartUploadStatus Status { get; }
}
