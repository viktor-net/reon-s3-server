namespace Reon.Core.Exceptions;

/// <summary>Бакет нельзя удалить, пока в нём остаются объекты или версии.</summary>
public sealed class BucketNotEmptyException : Exception
{
    public BucketNotEmptyException(string bucket)
        : base($"Bucket '{bucket}' is not empty. Remove all objects and versions first.")
    {
        Bucket = bucket;
    }

    public string Bucket { get; }
}
