using System.Net.WebSockets;
using System.Text;

namespace Reon.Api.Events;

public static class AdminEventsEndpoint
{
    /// <summary>
    /// Переводит запрос в WebSocket и передаёт административные события
    /// JSON-конвертами: { "type", "occurredAt", "bucket", "data" }.
    /// Необязательные query-параметры: <c>buckets</c> и <c>types</c> (через запятую).
    /// </summary>
    public static IEndpointRouteBuilder MapAdminEvents(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapGet("/api/admin/events", HandleAsync)
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task HandleAsync(
        HttpContext context,
        AdminEventHub hub,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "WebSocket upgrade required",
                    Detail = "This endpoint accepts a WebSocket connection: ws(s)://<host>/api/admin/events.",
                    Instance = context.Request.Path,
                },
                cancellationToken);

            return;
        }

        var buckets = ParseSet(context.Request.Query["buckets"]);
        var types = ParseSet(context.Request.Query["types"]);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var subscription = hub.Add(socket, buckets, types);

        var buffer = new byte[4096];

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None);

                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text || result.Count == 0)
                {
                    continue;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                // Протокол односторонний, от сервера к клиенту; единственное сообщение
                // клиента — лёгкий keepalive, который мы возвращаем обратно.
                if (message.Contains("\"ping\"", StringComparison.OrdinalIgnoreCase))
                {
                    await hub.SendTextAsync(
                        subscription,
                        "{\"type\":\"pong\"}",
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Запрос отменён.
        }
        catch (WebSocketException)
        {
            // Клиент оборвал соединение.
        }
        finally
        {
            hub.Remove(subscription.Id);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // Пир уже отключился.
                }
            }
        }
    }

    private static HashSet<string> ParseSet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
