using Microsoft.EntityFrameworkCore;

namespace Platform.Infrastructure.Persistence;

/// <summary>
/// The platform-owned database context.
///
/// ARCHITECTURE DECISION (Stage 3):
/// The architecture (see Architecture & Solution Design.md §32, §36) requires that:
///   1. One physical SQLite file is used for the entire application.
///   2. Each business module logically owns its own tables and migrations.
///   3. The platform coordinates migration execution.
///   4. The platform should NOT contain the business schema of every module.
///
/// Therefore the design uses MULTIPLE DbContexts sharing ONE SQLite connection:
///   - PlatformDbContext (this class) — platform-level concerns only
///   - {Module}DbContext per module — each module defines its own DbContext (Stage 5+)
///
/// All DbContexts share the same SQLite file but each owns its own schema prefix/tables.
/// Module DbContexts will be discovered and registered when modules are loaded (Stage 4+).
///
/// CURRENT STATE (Stage 3):
/// PlatformDbContext contains no tables. This is intentional.
/// There are currently no platform-level entities (audit, users, etc.).
/// Those will be introduced in their respective stages.
/// The DbContext exists to:
///   - Establish the SQLite connection infrastructure.
///   - Validate that EF Core + SQLite work correctly.
///   - Provide a foundation for platform-level tables when needed.
///   - Run database initialization and ensure migrations table is bootstrapped.
///
/// FUTURE:
/// Platform-level tables (if needed) will be added here:
///   - ApplicationMetadata (version, install date)
///   - MigrationLog (coordination across module migrations)
/// These are NOT added prematurely in Stage 3.
/// </summary>
public sealed class PlatformDbContext : DbContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations defined in this assembly.
        // Currently there are none (no platform tables in Stage 3).
        // When platform tables are needed, IEntityTypeConfiguration<T> implementations
        // will be placed in Platform.Infrastructure/Persistence/Configurations/ and
        // picked up automatically by this call.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);
    }
}
