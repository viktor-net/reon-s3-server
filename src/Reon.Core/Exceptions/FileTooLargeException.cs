namespace Reon.Core.Exceptions;

public sealed class FileTooLargeException : Exception
{
    public FileTooLargeException(long maxBytes)
        : base($"The uploaded file exceeds the maximum allowed size of {maxBytes} bytes.")
    {
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
}
