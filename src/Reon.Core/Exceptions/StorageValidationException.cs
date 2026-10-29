namespace Reon.Core.Exceptions;

/// <summary>Raised when an administrative request fails validation.</summary>
public sealed class StorageValidationException : Exception
{
    public StorageValidationException(string message)
        : base(message)
    {
    }
}
