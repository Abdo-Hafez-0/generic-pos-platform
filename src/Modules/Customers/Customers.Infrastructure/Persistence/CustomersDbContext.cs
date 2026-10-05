using Microsoft.EntityFrameworkCore;
using Customers.Domain.Entities;

namespace Customers.Infrastructure.Persistence;

/// <summary>
/// The Customers module's DbContext. Owns ONLY the cus_* tables; shares the physical SQLite file with the other module
/// DbContexts but never defines, reads or writes another module's tables. No cross-module foreign keys.
/// Migration assembly: Customers.Infrastructure.
/// </summary>
public sealed class CustomersDbContext : DbContext
{
    public CustomersDbContext(DbContextOptions<CustomersDbContext> options) : base(options)
    {
    }

    public DbSet<Customer> Customers { get; set; } = null!;
    public DbSet<CustomerAddress> CustomerAddresses { get; set; } = null!;
    public DbSet<CustomerContact> CustomerContacts { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CustomersDbContext).Assembly);
    }
}
