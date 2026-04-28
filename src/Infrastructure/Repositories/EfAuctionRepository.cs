using AuctionSystem.Application.Abstractions;
using AuctionSystem.Domain;
using AuctionSystem.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionSystem.Infrastructure.Repositories;

public sealed class EfAuctionRepository : IAuctionRepository
{
    private readonly AppDbContext _db;
    private readonly ILogger<EfAuctionRepository> _logger;

    public EfAuctionRepository(AppDbContext db, ILogger<EfAuctionRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AuctionItem?> GetByIdForUpdateAsync(int id, CancellationToken cancellationToken = default) =>
        await _db.AuctionItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<AuctionItem?> GetFirstSnapshotAsync(CancellationToken cancellationToken = default) =>
        await _db.AuctionItems.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Bids mutate tracked entities; ensure we attach/update correctly.
        var count = await _db.SaveChangesAsync(cancellationToken);
        _logger.LogDebug("Saved {Count} change(s) to auction store.", count);
    }
}
