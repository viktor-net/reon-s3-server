namespace Reon.Core.Interfaces;

/// <summary>
/// Журнал только на добавление, общий для файлового API и административного фасада.
/// Запись никогда не бросает исключение: аудит не должен подменять результат операции.
/// </summary>
public interface IStorageAuditor
{
    Task RecordAsync(
        string actor,
        string action,
        string resource,
        AuditResult result,
        int? statusCode,
        object? details,
        CancellationToken cancellationToken);
}
