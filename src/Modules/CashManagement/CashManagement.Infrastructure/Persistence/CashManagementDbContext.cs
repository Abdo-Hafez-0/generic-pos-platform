using Microsoft.EntityFrameworkCore;
using CashManagement.Domain.Entities;

namespace CashManagement.Infrastructure.Persistence;

/// <summary>
/// The CashManagement module's DbContext. Owns ONLY the cash_* tables; shares the physical SQLite file with the other module
/// DbContexts but never defines, reads or writes another module's tables. No cross-module foreign keys.
/// Migration assembly: CashManagement.Infrastructure.
/// </summary>
public sealed class CashManagementDbContext : DbContext
{
    public CashManagementDbContext(DbContextOptions<CashManagementDbContext> options) : base(options)
    {
    }

    public DbSet<CashSession> CashSessions { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CashManagementDbContext).Assembly);
    }
}
