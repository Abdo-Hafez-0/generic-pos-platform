using Microsoft.EntityFrameworkCore;
using Inventory.Domain.Entities;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The Inventory module's DbContext.
///
/// ARCHITECTURE:
/// - Owns: inv_Warehouses, inv_Locations, inv_StockItems, inv_StockMovements,
///         inv_StockAdjustments, inv_InventoryBalances tables.
/// - Shares the same physical SQLite file as PlatformDbContext and CatalogDbContext.
/// - Connection string resolved from configuration (same as Platform's).
/// - Migration assembly: Inventory.Infrastructure (owns its own migrations).
/// - No Platform tables are defined here. No Catalog tables are defined here.
///
/// TABLE NAMING: all Inventory tables are prefixed with "inv_" to avoid collision.
///
/// Architecture reference: §32 (Database Ownership), §36 (Module Migrations).
/// </summary>
public sealed class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options)
        : base(options)
    {
    }

    public DbSet<Warehouse> Warehouses { get; set; } = null!;
    public DbSet<Location> Locations { get; set; } = null!;
    public DbSet<StockItem> StockItems { get; set; } = null!;
    public DbSet<StockMovement> StockMovements { get; set; } = null!;
    public DbSet<StockAdjustment> StockAdjustments { get; set; } = null!;
    public DbSet<InventoryBalance> InventoryBalances { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations defined in this assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(InventoryDbContext).Assembly);
    }
}
