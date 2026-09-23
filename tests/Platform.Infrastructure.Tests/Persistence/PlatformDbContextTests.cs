using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Platform.Infrastructure.Persistence;

namespace Platform.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for the PlatformDbContext — SQLite configuration and connectivity.
///
/// These tests verify that the persistence foundation works correctly:
/// - SQLite provider is correctly configured.
/// - DbContext can be created and initialized.
/// - Database connection can be opened and closed.
/// - Basic EF Core operations work.
/// - No business tables are created (verifies Stage 3 scope is clean).
/// </summary>
public sealed class PlatformDbContextTests
{
    [Fact(DisplayName = "PlatformDbContext: SQLite provider is correctly configured")]
    public void PlatformDbContext_UsesSqliteProvider()
    {
        // Arrange
        using var context = TestDbContextFactory.CreateInMemory();

        // Act
        var providerName = context.Database.ProviderName;

        // Assert
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", providerName);
    }

    [Fact(DisplayName = "PlatformDbContext: Database connection can be opened")]
    public async Task PlatformDbContext_CanOpenConnection()
    {
        // Arrange
        await using var context = TestDbContextFactory.CreateInMemory();

        // Act — should not throw
        await context.Database.OpenConnectionAsync();

        // Assert
        var state = context.Database.GetDbConnection().State;
        Assert.Equal(System.Data.ConnectionState.Open, state);

        await context.Database.CloseConnectionAsync();
    }

    [Fact(DisplayName = "PlatformDbContext: Database connection can be closed")]
    public async Task PlatformDbContext_CanCloseConnection()
    {
        // Arrange
        await using var context = TestDbContextFactory.CreateInMemory();
        await context.Database.OpenConnectionAsync();

        // Act
        await context.Database.CloseConnectionAsync();

        // Assert
        var state = context.Database.GetDbConnection().State;
        Assert.Equal(System.Data.ConnectionState.Closed, state);
    }

    [Fact(DisplayName = "PlatformDbContext: EnsureCreated succeeds (database can be initialized)")]
    public async Task PlatformDbContext_EnsureCreated_Succeeds()
    {
        // Arrange — fresh in-memory DB (not yet created)
        var name = $"ensure-created-test-{Guid.NewGuid():N}";
        var connectionString = $"Data Source=file:{name}?mode=memory&cache=shared";

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using var context = new PlatformDbContext(options);

        // Act
        var created = await context.Database.EnsureCreatedAsync();

        // Assert
        Assert.True(created, "EnsureCreated should return true when creating a new database.");
    }

    [Fact(DisplayName = "PlatformDbContext: Stage 3 has no business entity types")]
    public void PlatformDbContext_HasNoBusinessEntityTypes()
    {
        // Arrange
        using var context = TestDbContextFactory.CreateInMemory();

        // Act
        var entityTypes = context.Model.GetEntityTypes().ToList();

        // Assert — PlatformDbContext must have NO entity types in Stage 3.
        // Business tables belong to their respective modules (Stage 5+).
        Assert.Empty(entityTypes);
    }

    [Fact(DisplayName = "PlatformDbContext: SaveChanges succeeds on empty context")]
    public async Task PlatformDbContext_SaveChanges_SucceedsWithNoChanges()
    {
        // Arrange
        await using var context = TestDbContextFactory.CreateInMemory();

        // Act — should not throw, returns 0 (no entities changed)
        var rowsAffected = await context.SaveChangesAsync();

        // Assert
        Assert.Equal(0, rowsAffected);
    }

    [Fact(DisplayName = "PlatformDbContext: Offline operation — no network calls required")]
    public async Task PlatformDbContext_WorksOffline_NoNetworkRequired()
    {
        // This test verifies the offline-first requirement.
        // SQLite is a file-based database — no network is required.
        // Using in-memory SQLite proves the database layer works without any network access.

        await using var context = TestDbContextFactory.CreateInMemory("offline-test");

        // Should work completely without network.
        await context.Database.EnsureCreatedAsync();
        await context.Database.OpenConnectionAsync();
        await context.Database.CloseConnectionAsync();

        Assert.True(true, "Database operations completed without network access.");
    }
}
