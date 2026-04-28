namespace AuctionSystem.Application.Dtos;

public sealed record PlaceBidResultDto(bool Success, string? ErrorMessage, AuctionStateDto? Auction);
