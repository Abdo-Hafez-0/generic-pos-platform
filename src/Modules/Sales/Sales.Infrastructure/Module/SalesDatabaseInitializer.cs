using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sales.Infrastructure.Persistence;

namespace Sales.Infrastructure.Module;

/// <summary>
/// Hosted service that applies pending Sales EF Core migrations during application startup.
///
/// DESIGN DECISION: Each module owns its migration runner (registered as IHostedService).
/// This keeps Platform independent of module migration concerns.
/// This service handles SalesDbContext only.
///
/// Startup order (see App.xaml.cs):
///   Platform DB → Catalog DB → Inventory DB → Sales DB → Application starts
///
/// Architecture reference: §36 (Local Database Migrations).
/// </summary>
public sealed class SalesDatabaseInitializer(
    IServiceProvider serviceProvider,
    ILogger<SalesDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Sales database schema...");

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SalesDbContext>();

            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
            var pendingList = pendingMigrations.ToList();

            if (pendingList.Count > 0)
            {
                logger.LogInformation(
                    "Applying {Count} pending Sales migration(s): {Migrations}",
                    pendingList.Count,
                    string.Join(", ", pendingList));

                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Sales migrations applied successfully.");
            }
            else
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("Sales database schema is up to date.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Sales database schema.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
