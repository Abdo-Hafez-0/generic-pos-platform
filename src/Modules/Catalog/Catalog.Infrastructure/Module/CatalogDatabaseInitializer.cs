using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Catalog.Infrastructure.Persistence;

namespace Catalog.Infrastructure.Module;

/// <summary>
/// Hosted service that applies pending Catalog EF Core migrations during application startup.
///
/// DESIGN DECISION: Each module owns its migration runner (registered as IHostedService).
/// This keeps Platform independent of module migration concerns.
/// The Platform's DatabaseInitializerService handles PlatformDbContext only.
/// This service handles CatalogDbContext only.
///
/// Architecture reference: §36 (Local Database Migrations).
/// </summary>
public sealed class CatalogDatabaseInitializer(
    IServiceProvider serviceProvider,
    ILogger<CatalogDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Catalog database schema...");

        try
        {
            // Use a scope to resolve the scoped CatalogDbContext
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
            var pendingList = pendingMigrations.ToList();

            if (pendingList.Count > 0)
            {
                logger.LogInformation(
                    "Applying {Count} pending Catalog migration(s): {Migrations}",
                    pendingList.Count,
                    string.Join(", ", pendingList));

                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Catalog migrations applied successfully.");
            }
            else
            {
                // No migrations exist yet — use EnsureCreated() to create the schema.
                // When the first migration is added, MigrateAsync() takes over.
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("Catalog database schema is up to date.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Catalog database schema.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
