using AuctionSystem.Application.Abstractions;
using AuctionSystem.Application.Dtos;
using AuctionSystem.Domain;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Application.Services;

/// <summary>
/// Orchestrates load → domain bid rules → persist → WebSocket broadcast.
/// </summary>
public sealed class AuctionService
{
    private const int DefaultAuctionId = 1;
    private readonly IAuctionRepository _repository;
    private readonly IAuctionRealtimeNotifier _notifier;
    private readonly ILogger<AuctionService> _logger;

    public AuctionService(
        IAuctionRepository repository,
        IAuctionRealtimeNotifier notifier,
        ILogger<AuctionService> logger)
    {
        _repository = repository;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<AuctionStateDto?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var item = await _repository.GetFirstSnapshotAsync(cancellationToken);
        return item is null ? null : AuctionStateDtoMapper.FromEntity(item);
    }

    public async Task<PlaceBidResultDto> PlaceBidAsync(decimal bidAmount, CancellationToken cancellationToken = default)
    {
        var item = await _repository.GetByIdForUpdateAsync(DefaultAuctionId, cancellationToken);
        if (item is null)
        {
            return new PlaceBidResultDto(false, "Auction not found.", null);
        }

        var result = item.TryApplyBid(bidAmount);
        if (!result.Success)
        {
            _logger.LogWarning("Rejected bid {BidAmount}: {Reason}", bidAmount, result.ErrorMessage);
            return new PlaceBidResultDto(false, result.ErrorMessage, AuctionStateDtoMapper.FromEntity(item));
        }

        await _repository.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Accepted bid {BidAmount}; new price {Price}", bidAmount, item.CurrentPrice);

        await _notifier.BroadcastAuctionStateAsync(item, cancellationToken);

        return new PlaceBidResultDto(true, null, AuctionStateDtoMapper.FromEntity(item));
    }
}
