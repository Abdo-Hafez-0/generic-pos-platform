using Microsoft.EntityFrameworkCore;
using Catalog.Domain.Entities;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// The Catalog module's DbContext.
///
/// ARCHITECTURE:
/// - Owns: cat_Products, cat_Categories, cat_Units, cat_Barcodes tables.
/// - Shares the same physical SQLite file as PlatformDbContext and future module contexts.
/// - Connection string resolved from configuration (same as Platform's).
/// - Migration assembly: Catalog.Infrastructure (owns its own migrations).
/// - No Platform tables are defined here. No other module tables are defined here.
///
/// TABLE NAMING: all Catalog tables are prefixed with "cat_" to avoid collision
/// with other modules sharing the same SQLite file.
///
/// Architecture reference: §32 (Database Ownership), §36 (Module Migrations).
/// </summary>
public sealed class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products { get; set; } = null!;
    public DbSet<Category> Categories { get; set; } = null!;
    public DbSet<Unit> Units { get; set; } = null!;
    public DbSet<Barcode> Barcodes { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations defined in this assembly.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CatalogDbContext).Assembly);
    }
}
