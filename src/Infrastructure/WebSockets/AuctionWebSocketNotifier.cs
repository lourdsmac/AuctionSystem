using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionSystem.Application.Abstractions;
using AuctionSystem.Application.Dtos;
using AuctionSystem.Domain;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Infrastructure.WebSockets;

/// <summary>
/// Implements Application notifier by pushing JSON snapshots to every open WebSocket.
/// </summary>
public sealed class AuctionWebSocketNotifier : IAuctionRealtimeNotifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WebSocketConnectionManager _connections;
    private readonly ILogger<AuctionWebSocketNotifier> _logger;

    public AuctionWebSocketNotifier(
        WebSocketConnectionManager connections,
        ILogger<AuctionWebSocketNotifier> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    public async Task BroadcastAuctionStateAsync(AuctionItem item, CancellationToken cancellationToken = default)
    {
        var dto = AuctionStateDtoMapper.FromEntity(item);
        var envelope = new RealtimeEnvelope("auction_update", dto);
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        await _connections.BroadcastTextAsync(json, cancellationToken);
        _logger.LogInformation(
            "Broadcast auction update price={Price} to {ConnectionCount} connection(s)",
            item.CurrentPrice,
            _connections.ConnectionCount);
    }

    private sealed record RealtimeEnvelope(string Type, AuctionStateDto Payload);
}
