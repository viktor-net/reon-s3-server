namespace Reon.Core.Interfaces;

/// <summary>
/// Transport-agnostic publisher for administrative real-time events.
/// The API layer implements it on top of WebSockets.
/// </summary>
public interface IStorageEventPublisher
{
    ValueTask PublishAsync(StorageEvent storageEvent, CancellationToken cancellationToken = default);
}
