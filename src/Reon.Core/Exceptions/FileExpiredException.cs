namespace Reon.Core.Exceptions;

/// <summary>
/// Срок хранения файла истёк. Содержимое может ещё находиться в S3 до прохода
/// сборщика мусора, но пользователю файл уже недоступен.
/// </summary>
public sealed class FileExpiredException : Exception
{
    public FileExpiredException(Guid fileId, DateTimeOffset? expiresAt)
        : base($"File '{fileId}' expired at {expiresAt:O}.")
    {
        FileId = fileId;
        ExpiresAt = expiresAt;
    }

    public Guid FileId { get; }

    public DateTimeOffset? ExpiresAt { get; }
}
