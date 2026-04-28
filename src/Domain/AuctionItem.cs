namespace AuctionSystem.Domain;

/// <summary>
/// Auction aggregate root. Price changes only through <see cref="TryApplyBid"/> so rules stay centralized.
/// </summary>
public sealed class AuctionItem
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal CurrentPrice { get; set; }

    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// Domain rule: a bid must strictly exceed the current price; timestamp updates on success.
    /// </summary>
    public AuctionBidResult TryApplyBid(decimal bidAmount)
    {
        if (bidAmount <= CurrentPrice)
        {
            return AuctionBidResult.Failed($"Bid must be higher than current price ({CurrentPrice}).");
        }

        CurrentPrice = bidAmount;
        LastUpdated = DateTime.UtcNow;
        return AuctionBidResult.Succeeded();
    }
}
