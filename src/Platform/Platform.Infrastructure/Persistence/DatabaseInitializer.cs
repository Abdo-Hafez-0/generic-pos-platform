using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Platform.Infrastructure.Persistence;

/// <summary>
/// Coordinates database initialization for the platform context.
///
/// Responsibilities:
/// - Apply pending EF Core migrations for the platform context.
/// - Verify the database is accessible (connectivity check).
/// - Log initialization results.
///
/// IMPORTANT: This service does NOT destroy or recreate data.
/// - EnsureDeleted() is NEVER called.
/// - EnsureCreated() is used ONLY as a fallback when no migrations exist.
///   When migrations exist (Stage 4+), MigrateAsync() is used exclusively.
///
/// Module-owned migrations are NOT run here.
/// Stage 4 (Module Contract) will define how module migrations are discovered and executed.
///
/// OFFLINE DESIGN:
/// - No network calls are made.
/// - All operations are local SQLite file operations.
/// - If the database file doesn't exist, SQLite creates it automatically.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly PlatformDbContext _context;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        PlatformDbContext context,
        ILogger<DatabaseInitializer> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Initializes the platform database.
    /// Creates the database file if it doesn't exist.
    /// Applies pending platform migrations if any exist.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing platform database...");

        try
        {
            // Check if there are any pending migrations.
            var pendingMigrations = await _context.Database
                .GetPendingMigrationsAsync(cancellationToken);

            var pendingList = pendingMigrations.ToList();

            if (pendingList.Count > 0)
            {
                _logger.LogInformation(
                    "Applying {Count} pending platform migration(s): {Migrations}",
                    pendingList.Count,
                    string.Join(", ", pendingList));

                await _context.Database.MigrateAsync(cancellationToken);

                _logger.LogInformation("Platform migrations applied successfully.");
            }
            else
            {
                // No migrations registered yet (Stage 3 — PlatformDbContext is empty).
                // EnsureCreated() creates the database file and applies the current model
                // without creating a migrations history table.
                // This is appropriate for Stage 3 where there are no platform tables.
                // When the first platform migration is added, MigrateAsync() takes over.
                var created = await _context.Database.EnsureCreatedAsync(cancellationToken);

                if (created)
                {
                    _logger.LogInformation(
                        "Platform database created at: {DatabasePath}",
                        GetDatabasePath());
                }
                else
                {
                    _logger.LogInformation(
                        "Platform database already exists. No pending migrations.");
                }
            }

            // Verify we can open and use the connection.
            await _context.Database.OpenConnectionAsync(cancellationToken);
            await _context.Database.CloseConnectionAsync();

            _logger.LogInformation("Platform database initialization complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to initialize platform database. " +
                "The application cannot continue without a working local database.");
            throw;
        }
    }

    private string GetDatabasePath()
    {
        var connection = _context.Database.GetConnectionString();
        return connection ?? "unknown";
    }
}
