using Microsoft.EntityFrameworkCore;
using Suppliers.Domain.Entities;

namespace Suppliers.Infrastructure.Persistence;

/// <summary>
/// The Suppliers module's DbContext. Owns ONLY the sup_* tables; shares the physical SQLite file with the other module
/// DbContexts but never defines, reads or writes another module's tables. No cross-module foreign keys.
/// Migration assembly: Suppliers.Infrastructure.
/// </summary>
public sealed class SuppliersDbContext : DbContext
{
    public SuppliersDbContext(DbContextOptions<SuppliersDbContext> options) : base(options)
    {
    }

    public DbSet<Supplier> Suppliers { get; set; } = null!;
    public DbSet<SupplierAddress> SupplierAddresses { get; set; } = null!;
    public DbSet<SupplierContact> SupplierContacts { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SuppliersDbContext).Assembly);
    }
}
