namespace Reon.Core.Exceptions;

public sealed class ObjectStorageException : Exception
{
    public ObjectStorageException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
