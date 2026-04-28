namespace AuctionSystem.Domain;

public sealed class AuctionBidResult
{
    private AuctionBidResult(bool success, string? errorMessage)
    {
        Success = success;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }

    public string? ErrorMessage { get; }

    public static AuctionBidResult Succeeded() => new(true, null);

    public static AuctionBidResult Failed(string message) => new(false, message);
}
