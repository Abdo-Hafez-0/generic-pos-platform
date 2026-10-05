using Microsoft.EntityFrameworkCore;
using Pricing.Domain.Entities;

namespace Pricing.Infrastructure.Persistence;

/// <summary>
/// The Pricing module's DbContext. Owns ONLY the pri_* tables; shares the physical SQLite file with the other module
/// DbContexts but never defines, reads or writes another module's tables. No cross-module foreign keys.
/// Migration assembly: Pricing.Infrastructure.
/// </summary>
public sealed class PricingDbContext : DbContext
{
    public PricingDbContext(DbContextOptions<PricingDbContext> options) : base(options)
    {
    }

    public DbSet<PriceList> PriceLists { get; set; } = null!;
    public DbSet<Price> Prices { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PricingDbContext).Assembly);
    }
}
