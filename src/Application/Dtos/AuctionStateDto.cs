using AuctionSystem.Domain;

namespace AuctionSystem.Application.Dtos;

public sealed record AuctionStateDto(
    int Id,
    string Name,
    decimal CurrentPrice,
    DateTime LastUpdated);

public static class AuctionStateDtoMapper
{
    public static AuctionStateDto FromEntity(AuctionItem item) =>
        new(item.Id, item.Name, item.CurrentPrice, item.LastUpdated);
}
