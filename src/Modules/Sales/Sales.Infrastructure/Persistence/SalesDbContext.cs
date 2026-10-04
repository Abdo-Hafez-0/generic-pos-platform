using Microsoft.EntityFrameworkCore;
using Sales.Domain.Entities;

namespace Sales.Infrastructure.Persistence;

/// <summary>
/// The Sales module's DbContext.
///
/// ARCHITECTURE:
/// - Owns: sal_Sales, sal_SaleItems, sal_Returns, sal_ReturnItems, sal_SalesTransactions tables.
/// - Shares the same physical SQLite file as PlatformDbContext, CatalogDbContext, and InventoryDbContext.
/// - Connection string resolved from configuration (same as Platform's).
/// - Migration assembly: Sales.Infrastructure (owns its own migrations).
/// - No Platform, Catalog, or Inventory tables are defined here.
///
/// TABLE NAMING: all Sales tables are prefixed with "sal_" to avoid collision.
///
/// Architecture reference: §32 (Database Ownership), §36 (Module Migrations).
/// </summary>
public sealed class SalesDbContext : DbContext
{
    public SalesDbContext(DbContextOptions<SalesDbContext> options)
        : base(options)
    {
    }

    public DbSet<Sale> Sales { get; set; } = null!;
    public DbSet<SaleItem> SaleItems { get; set; } = null!;
    public DbSet<Return> Returns { get; set; } = null!;
    public DbSet<ReturnItem> ReturnItems { get; set; } = null!;
    public DbSet<SalesTransaction> SalesTransactions { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations defined in this assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SalesDbContext).Assembly);
    }
}
