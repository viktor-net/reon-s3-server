namespace Reon.Core.Interfaces;

/// <summary>
/// Реестр возможностей S3-провайдера. Значения хранятся в Postgres, чтобы
/// переживать перезапуск и не требовать ручной синхронизации конфигурации.
/// </summary>
public interface IStorageCapabilityStore
{
    /// <summary>Текущий срез возможностей. Отсутствующие записи берутся из конфига.</summary>
    Task<StorageCapabilities> GetAsync(CancellationToken cancellationToken);

    /// <summary>Записывает базовые значения из конфига, если записей ещё нет.</summary>
    Task EnsureSeededAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Помечает возможность как недоступную. Вызывается, когда реальная
    /// операция провайдера вернула 501.
    /// </summary>
    Task MarkUnsupportedAsync(
        StorageCapability capability,
        string? details,
        CancellationToken cancellationToken);
}
