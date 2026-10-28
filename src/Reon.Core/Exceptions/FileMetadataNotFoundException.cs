namespace Reon.Core.Exceptions;

public sealed class FileMetadataNotFoundException : Exception
{
    public FileMetadataNotFoundException(Guid fileId)
        : base($"File '{fileId}' was not found.")
    {
        FileId = fileId;
    }

    public Guid FileId { get; }
}
