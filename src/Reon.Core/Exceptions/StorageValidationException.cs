namespace Reon.Core.Exceptions;

/// <summary>Возникает, когда административный запрос не проходит валидацию.</summary>
public sealed class StorageValidationException : Exception
{
    public StorageValidationException(string message)
        : base(message)
    {
    }
}
