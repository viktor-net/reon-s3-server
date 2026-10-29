namespace Reon.Core.Exceptions;

public sealed class BucketAlreadyExistsException : Exception
{
    public BucketAlreadyExistsException(string bucket)
        : base($"Bucket '{bucket}' already exists.")
    {
        Bucket = bucket;
    }

    public string Bucket { get; }
}
