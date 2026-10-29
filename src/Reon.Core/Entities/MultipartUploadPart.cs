namespace Reon.Core.Entities;

public sealed class MultipartUploadPart
{
    private MultipartUploadPart()
    {
        // Required by EF Core.
    }

    public Guid UploadId { get; private set; }

    public int PartNumber { get; private set; }

    public string ETag { get; private set; } = string.Empty;

    public long Size { get; private set; }

    public DateTimeOffset UploadedAt { get; private set; }

    public static MultipartUploadPart Create(
        Guid uploadId,
        int partNumber,
        string etag,
        long size,
        DateTimeOffset now)
    {
        return new MultipartUploadPart
        {
            UploadId = uploadId,
            PartNumber = partNumber,
            ETag = etag,
            Size = size,
            UploadedAt = now,
        };
    }

    public void Refresh(string etag, long size, DateTimeOffset now)
    {
        ETag = etag;
        Size = size;
        UploadedAt = now;
    }
}
