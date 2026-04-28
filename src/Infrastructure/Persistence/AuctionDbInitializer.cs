using AuctionSystem.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Infrastructure.Persistence;

/// <summary>
/// Seeds the in-memory auction used by both SSE (read) and WebSocket (read/write) flows.
/// </summary>
public static class AuctionDbInitializer
{
    public static void Initialize(AppDbContext db, ILogger logger)
    {
        db.Database.EnsureCreated();

        if (db.AuctionItems.Any())
        {
            return;
        }

        db.AuctionItems.Add(new AuctionItem
        {
            Id = 1,
            Name = "Vintage Watch",
            CurrentPrice = 100m,
            LastUpdated = DateTime.UtcNow,
        });

        db.SaveChanges();
        logger.LogInformation("Seeded demo auction item (Id=1, starting price 100).");
    }
}
