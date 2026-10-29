using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Reon.Api.Events;

/// <summary>
/// Реестр административных WebSocket-подписчиков и публикатор, которым
/// пользуются сервисы фасада. События фильтруются для каждого соединения
/// </summary>
public sealed partial class AdminEventHub(ILogger<AdminEventHub> logger) : IStorageEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, AdminEventSubscription> _subscriptions = new();

    public AdminEventSubscription Add(
        WebSocket socket,
        IReadOnlySet<string> buckets,
        IReadOnlySet<string> types)
    {
        var subscription = new AdminEventSubscription(socket, buckets, types);
        _subscriptions[subscription.Id] = subscription;
        return subscription;
    }

    public void Remove(Guid subscriptionId)
    {
        if (_subscriptions.TryRemove(subscriptionId, out var subscription))
        {
            subscription.Dispose();
        }
    }

    public async ValueTask PublishAsync(
        StorageEvent storageEvent,
        CancellationToken cancellationToken = default)
    {
        if (_subscriptions.IsEmpty)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                type = storageEvent.Type,
                occurredAt = storageEvent.OccurredAt,
                bucket = storageEvent.Bucket,
                data = storageEvent.Data,
            },
            JsonOptions);

        foreach (var subscription in _subscriptions.Values)
        {
            if (!subscription.Matches(storageEvent.Type, storageEvent.Bucket))
            {
                continue;
            }

            try
            {
                await subscription.SendAsync(payload, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Клиент отключился или сокет сломан: удаляем подписку.
                LogSendFailed(logger, exception, subscription.Id);
                Remove(subscription.Id);
            }
        }
    }

    public async Task SendTextAsync(
        AdminEventSubscription subscription,
        string json,
        CancellationToken cancellationToken)
    {
        await subscription.SendAsync(System.Text.Encoding.UTF8.GetBytes(json), cancellationToken);
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Debug, Message = "Dropping admin event subscription {SubscriptionId}: send failed.")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, Guid subscriptionId);
}

public sealed class AdminEventSubscription : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IReadOnlySet<string> _buckets;
    private readonly IReadOnlySet<string> _types;

    public AdminEventSubscription(
        WebSocket socket,
        IReadOnlySet<string> buckets,
        IReadOnlySet<string> types)
    {
        Id = Guid.NewGuid();
        Socket = socket;
        _buckets = buckets;
        _types = types;
    }

    public Guid Id { get; }

    public WebSocket Socket { get; }

    public bool Matches(string type, string? bucket)
    {
        if (_types.Count > 0 && !_types.Contains(type))
        {
            return false;
        }

        return _buckets.Count == 0 || bucket is null || _buckets.Contains(bucket);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (Socket.State != WebSocketState.Open)
        {
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (Socket.State != WebSocketState.Open)
            {
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            }

            await Socket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
