namespace Reon.Core.Interfaces;

/// <summary>
/// Best-effort защита записи: отклоняет запись, которая превысит настроенную
/// квоту бакета. Использование кешируется, поэтому проверка приблизительная.
/// </summary>
public interface IStorageQuotaGuard
{
    Task EnsureCanWriteAsync(
        string bucket,
        long additionalBytes,
        CancellationToken cancellationToken);
}
