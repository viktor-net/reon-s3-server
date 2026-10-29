namespace Reon.Core.Exceptions;

public sealed class QuotaExceededException : Exception
{
    public QuotaExceededException(string bucket, long usedBytes, long quotaBytes)
        : base($"Bucket '{bucket}' quota exceeded: {usedBytes} of {quotaBytes} bytes used.")
    {
        Bucket = bucket;
        UsedBytes = usedBytes;
        QuotaBytes = quotaBytes;
    }

    public string Bucket { get; }

    public long UsedBytes { get; }

    public long QuotaBytes { get; }
}
