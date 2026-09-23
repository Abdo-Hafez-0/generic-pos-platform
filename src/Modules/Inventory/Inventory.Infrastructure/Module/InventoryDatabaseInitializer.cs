using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Infrastructure.Module;

/// <summary>
/// Hosted service that applies pending Inventory EF Core migrations during application startup.
///
/// DESIGN DECISION: Each module owns its migration runner (registered as IHostedService).
/// This keeps Platform independent of module migration concerns.
/// This service handles InventoryDbContext only.
///
/// Startup order (see App.xaml.cs):
///   Platform DB → Catalog DB → Inventory DB → Application starts
///
/// Architecture reference: §36 (Local Database Migrations).
/// </summary>
public sealed class InventoryDatabaseInitializer(
    IServiceProvider serviceProvider,
    ILogger<InventoryDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Inventory database schema...");

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
            var pendingList = pendingMigrations.ToList();

            if (pendingList.Count > 0)
            {
                logger.LogInformation(
                    "Applying {Count} pending Inventory migration(s): {Migrations}",
                    pendingList.Count,
                    string.Join(", ", pendingList));

                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Inventory migrations applied successfully.");
            }
            else
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("Inventory database schema is up to date.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Inventory database schema.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
