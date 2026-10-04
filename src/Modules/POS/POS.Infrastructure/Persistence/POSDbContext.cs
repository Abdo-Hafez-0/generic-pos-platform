using Microsoft.EntityFrameworkCore;
using POS.Domain.Entities;

namespace POS.Infrastructure.Persistence;

/// <summary>
/// The POS module DbContext.
///
/// ARCHITECTURE:
/// - Owns: pos_Sessions, pos_Carts, pos_CartItems tables.
/// - Shares the same physical SQLite file as the Platform, Catalog, Inventory and Sales DbContexts.
/// - Migration assembly: POS.Infrastructure (owns its own migrations).
/// - Holds only plain Guid references to other modules (product, warehouse, sale) - no cross-module
///   foreign keys and no other module tables.
///
/// Architecture reference: §32 (Database Ownership), §36 (Module Migrations).
/// </summary>
public sealed class POSDbContext : DbContext
{
    public POSDbContext(DbContextOptions<POSDbContext> options)
        : base(options)
    {
    }

    public DbSet<PosSession> Sessions { get; set; } = null!;
    public DbSet<PosCart> Carts { get; set; } = null!;
    public DbSet<PosCartItem> CartItems { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(POSDbContext).Assembly);
    }
}
