namespace Reon.Core.Interfaces;

/// <summary>
/// Обнаруживает и устраняет расхождения между метаданными Postgres и объектным хранилищем.
/// </summary>
public interface IReconciliationService
{
    /// <summary>Завершает или помечает ошибкой строки Pending старше заданного таймаута.</summary>
    Task ReconcilePendingAsync(CancellationToken cancellationToken);

    /// <summary>Повторяет удаление в S3 для строк, застрявших в Deleting.</summary>
    Task ReconcileDeletingAsync(CancellationToken cancellationToken);

    /// <summary>Удаляет мягко удалённые строки после настроенного срока хранения.</summary>
    Task PurgeDeletedAsync(CancellationToken cancellationToken);

    /// <summary>Удаляет объекты без строки метаданных старше льготного периода.</summary>
    Task SweepOrphansAsync(CancellationToken cancellationToken);
}
