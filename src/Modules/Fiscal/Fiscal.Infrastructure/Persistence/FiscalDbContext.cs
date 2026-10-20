using Fiscal.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fiscal.Infrastructure.Persistence;

/// <summary>
/// The Fiscal module's DbContext. Owns ONLY the fis_* tables; shares the physical SQLite file with the other module DbContexts but never
/// defines, reads or writes another module's tables. No cross-module foreign keys. Migration assembly: Fiscal.Infrastructure.
/// </summary>
public sealed class FiscalDbContext : DbContext
{
    public FiscalDbContext(DbContextOptions<FiscalDbContext> options) : base(options)
    {
    }

    public DbSet<FiscalSettings> Settings { get; set; } = null!;
    public DbSet<FiscalTill> Tills { get; set; } = null!;
    public DbSet<FiscalDocument> Documents { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FiscalDbContext).Assembly);
    }
}
