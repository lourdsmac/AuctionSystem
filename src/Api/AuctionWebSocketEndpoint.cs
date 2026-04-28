using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AuctionSystem.Application.Dtos;
using AuctionSystem.Application.Services;
using AuctionSystem.Infrastructure.WebSockets;
using Serilog.Context;

namespace AuctionSystem.Api;

/// <summary>
/// WebSockets: accepts upgrade on <c>/ws/auction</c>, relays client bids to <see cref="AuctionService"/> and relies on infrastructure broadcast after successful bids.
/// </summary>
public static class AuctionWebSocketEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static WebApplication MapAuctionWebSocket(this WebApplication app)
    {
        app.Map("/ws/auction", async (
            HttpContext http,
            IServiceScopeFactory scopeFactory,
            WebSocketConnectionManager connections,
            ILoggerFactory loggerFactory) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsync(
                    "WebSocket upgrade required. Connect with ws:// or wss://",
                    CancellationToken.None);
                return;
            }

            using var ws = await http.WebSockets.AcceptWebSocketAsync();
            var log = loggerFactory.CreateLogger("Auction.WebSocket");

            log.LogInformation("WebSocket upgrade accepted (subprotocol default). Connections: {Count}", connections.ConnectionCount + 1);

            var connectionId = connections.Register(ws);

            try
            {
                using (LogContext.PushProperty("ConnectionId", connectionId))
                {
                    await SendCurrentState(ws, scopeFactory, http.RequestAborted);
                    await ListenLoop(ws, scopeFactory, log);
                }
            }
            finally
            {
                connections.Remove(connectionId);
                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                }
            }
        });

        return app;
    }

    /// <summary>Send one snapshot after connect so clients see price before bidding.</summary>
    private static async Task SendCurrentState(WebSocket ws, IServiceScopeFactory scopeFactory, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<AuctionService>();
        var state = await svc.GetCurrentAsync(ct);
        if (state is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(
            new { type = "connected", auction = state },
            JsonOpts);

        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task ListenLoop(WebSocket ws, IServiceScopeFactory scopeFactory, ILogger log)
    {
        var buffer = new byte[4096];

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                log.LogInformation("Client initiated close.");
                break;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(buffer.AsSpan(0, result.Count));

            BidMessage? bid;
            try
            {
                bid = JsonSerializer.Deserialize<BidMessage>(text, JsonOpts);
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Invalid JSON from client.");
                await SendJson(
                    ws,
                    new { type = "bid_rejected", error = "Invalid JSON. Expected { \"bidAmount\": number }." });
                continue;
            }

            if (bid?.BidAmount is null or <= 0)
            {
                await SendJson(ws, new { type = "bid_rejected", error = "bidAmount must be a positive number." });
                continue;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<AuctionService>();
            var bidResult = await svc.PlaceBidAsync(bid.BidAmount.Value);

            if (!bidResult.Success)
            {
                await SendJson(
                    ws,
                    new
                    {
                        type = "bid_rejected",
                        error = bidResult.ErrorMessage,
                        auction = bidResult.Auction,
                    });
            }
            // Success: AuctionService triggers IAuctionRealtimeNotifier → all clients get broadcast (including this one).
        }
    }

    private static async Task SendJson(WebSocket ws, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    /// <summary>Wire format from browser: <c>{ "bidAmount": 120 }</c></summary>
    private sealed record BidMessage(decimal? BidAmount);
}
