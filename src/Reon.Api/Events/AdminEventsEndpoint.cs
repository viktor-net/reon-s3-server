using System.Net.WebSockets;
using System.Text;

namespace Reon.Api.Events;

public static class AdminEventsEndpoint
{
    /// <summary>
    /// Upgrades the request to a WebSocket and streams administrative events as
    /// JSON envelopes: { "type", "occurredAt", "bucket", "data" }.
    /// Optional query parameters: <c>buckets</c> and <c>types</c> (comma separated).
    /// </summary>
    public static IEndpointRouteBuilder MapAdminEvents(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapGet("/api/v1/admin/events", HandleAsync)
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
                    Detail = "This endpoint accepts a WebSocket connection: ws(s)://<host>/api/v1/admin/events.",
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

                // The protocol is server-to-client; the only client message is a
                // lightweight keepalive that we echo back.
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
            // Request aborted.
        }
        catch (WebSocketException)
        {
            // Client disconnected abruptly.
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
                    // The peer is already gone.
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
