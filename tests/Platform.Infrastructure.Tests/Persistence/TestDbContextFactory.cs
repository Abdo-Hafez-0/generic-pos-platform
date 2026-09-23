using Microsoft.EntityFrameworkCore;
using Platform.Infrastructure.Persistence;

namespace Platform.Infrastructure.Tests.Persistence;

/// <summary>
/// Helper to create isolated PlatformDbContext instances for testing.
///
/// Uses SQLite in-memory databases (distinct per test via unique Data Sources).
/// This avoids test interference and doesn't touch the production database location.
///
/// Why SQLite in-memory rather than EF Core InMemory provider:
/// - The EF Core InMemory provider bypasses SQLite-specific behaviors.
/// - Using a real SQLite connection (even in-memory) validates actual SQL generation.
/// - Connection string: "Data Source=:memory:" creates a true in-memory SQLite database.
/// - For test isolation, each test uses a unique named in-memory database.
/// </summary>
internal static class TestDbContextFactory
{
    /// <summary>
    /// Creates an isolated PlatformDbContext using SQLite in-memory.
    /// The caller is responsible for disposing it.
    /// </summary>
    internal static PlatformDbContext CreateInMemory(string? databaseName = null)
    {
        var name = databaseName ?? $"test-db-{Guid.NewGuid():N}";
        var connectionString = $"Data Source=file:{name}?mode=memory&cache=shared";

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connectionString)
            .Options;

        var context = new PlatformDbContext(options);

        // Ensure database is created (schema applied).
        // EnsureCreated() is appropriate for tests — it creates the schema without migrations.
        context.Database.EnsureCreated();

        return context;
    }
}
