using AuctionSystem.Domain;

namespace AuctionSystem.Application.Abstractions;

/// <summary>
/// Notifies all WebSocket subscribers after a successful bid (Application does not reference SignalR/WebSockets).
/// </summary>
public interface IAuctionRealtimeNotifier
{
    Task BroadcastAuctionStateAsync(AuctionItem item, CancellationToken cancellationToken = default);
}
