namespace Reon.Core.Exceptions;

public sealed class BucketNotFoundException : Exception
{
    public BucketNotFoundException(string bucket)
        : base($"Bucket '{bucket}' does not exist.")
    {
        Bucket = bucket;
    }

    public string Bucket { get; }
}
