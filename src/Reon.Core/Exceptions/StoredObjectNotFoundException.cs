namespace Reon.Core.Exceptions;

public sealed class StoredObjectNotFoundException : Exception
{
    public StoredObjectNotFoundException(string bucket, string key)
        : base($"Object '{key}' does not exist in bucket '{bucket}'.")
    {
        Bucket = bucket;
        Key = key;
    }

    public string Bucket { get; }

    public string Key { get; }
}
