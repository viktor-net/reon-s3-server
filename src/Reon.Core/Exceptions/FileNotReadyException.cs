using Reon.Core.Enums;

namespace Reon.Core.Exceptions;

public sealed class FileNotReadyException : Exception
{
    public FileNotReadyException(Guid fileId, FileStatus status)
        : base($"File '{fileId}' is not available for download (status: {status}).")
    {
        FileId = fileId;
        Status = status;
    }

    public Guid FileId { get; }

    public FileStatus Status { get; }
}
