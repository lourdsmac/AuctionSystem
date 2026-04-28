using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Infrastructure.WebSockets;

/// <summary>
/// Tracks active WebSocket connections for fan-out broadcasts. Thread-safe for connect/disconnect during sends.
/// </summary>
public sealed class WebSocketConnectionManager
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _connections = new();
    private readonly ILogger<WebSocketConnectionManager> _logger;

    public WebSocketConnectionManager(ILogger<WebSocketConnectionManager> logger)
    {
        _logger = logger;
    }

    public int ConnectionCount => _connections.Count;

    public Guid Register(WebSocket socket)
    {
        var id = Guid.NewGuid();
        if (_connections.TryAdd(id, socket))
        {
            _logger.LogInformation("WebSocket registered {ConnectionId}. Active: {Count}", id, ConnectionCount);
        }

        return id;
    }

    public void Remove(Guid connectionId)
    {
        if (_connections.TryRemove(connectionId, out var socket))
        {
            _logger.LogInformation(
                "WebSocket removed {ConnectionId}. State={State}. Active: {Count}",
                connectionId,
                socket.State,
                ConnectionCount);
        }
    }

    public async Task BroadcastTextAsync(string message, CancellationToken cancellationToken)
    {
        var segment = System.Text.Encoding.UTF8.GetBytes(message);
        var dead = new List<Guid>();

        foreach (var (id, socket) in _connections)
        {
            if (socket.State != WebSocketState.Open)
            {
                dead.Add(id);
                continue;
            }

            try
            {
                await socket.SendAsync(
                    segment,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "Broadcast failed for {ConnectionId}; removing.", id);
                dead.Add(id);
            }
        }

        foreach (var id in dead)
        {
            Remove(id);
        }

        _logger.LogDebug("Broadcast to {Count} socket(s): {Snippet}", _connections.Count, Truncate(message));
    }

    private static string Truncate(string s, int max = 120) =>
        s.Length <= max ? s : s[..max] + "…";
}
