namespace Reon.Core.Exceptions;

public sealed class FileValidationException : Exception
{
    public FileValidationException(string message)
        : base(message)
    {
    }
}
