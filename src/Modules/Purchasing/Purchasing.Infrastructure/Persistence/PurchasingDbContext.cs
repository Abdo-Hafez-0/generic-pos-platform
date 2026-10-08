using Microsoft.EntityFrameworkCore;
using Purchasing.Domain.Entities;

namespace Purchasing.Infrastructure.Persistence;

/// <summary>
/// The Purchasing module's DbContext. Owns ONLY the pur_* tables; shares the physical SQLite file with the other module
/// DbContexts but never defines, reads or writes another module's tables. No cross-module foreign keys.
/// Migration assembly: Purchasing.Infrastructure.
/// </summary>
public sealed class PurchasingDbContext : DbContext
{
    public PurchasingDbContext(DbContextOptions<PurchasingDbContext> options) : base(options)
    {
    }

    public DbSet<PurchaseOrder> PurchaseOrders { get; set; } = null!;
    public DbSet<PurchaseOrderLine> PurchaseOrderLines { get; set; } = null!;
    public DbSet<SupplierReturn> SupplierReturns { get; set; } = null!;
    public DbSet<SupplierReturnLine> SupplierReturnLines { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PurchasingDbContext).Assembly);
    }
}
