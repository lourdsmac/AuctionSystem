using AuctionSystem.Domain;

namespace AuctionSystem.Application.Abstractions;

/// <summary>
/// Persistence for the single (demo) auction item; infrastructure provides EF implementation.
/// </summary>
public interface IAuctionRepository
{
    /// <summary>Read-only snapshot for API/SSE (no change tracking).</summary>
    Task<AuctionItem?> GetFirstSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Tracked entity for bid mutations (EF will persist changes).</summary>
    Task<AuctionItem?> GetByIdForUpdateAsync(int id, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
