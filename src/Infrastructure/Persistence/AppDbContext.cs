using AuctionSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace AuctionSystem.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<AuctionItem> AuctionItems => Set<AuctionItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuctionItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.CurrentPrice).HasPrecision(18, 2);
        });
    }
}
