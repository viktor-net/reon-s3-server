namespace Reon.Core.Interfaces;

/// <summary>
/// Публикатор административных событий реального времени, не зависящий от транспорта.
/// API-слой реализует его поверх WebSocket.
/// </summary>
public interface IStorageEventPublisher
{
    ValueTask PublishAsync(StorageEvent storageEvent, CancellationToken cancellationToken = default);
}
