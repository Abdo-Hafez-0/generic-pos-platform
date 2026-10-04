using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using POS.Infrastructure.Persistence;

namespace POS.Infrastructure.Module;

/// <summary>
/// Hosted service that applies pending POS EF Core migrations during application startup.
///
/// DESIGN DECISION: Each module owns its migration runner (registered as IHostedService).
/// This keeps Platform independent of module migration concerns.
/// This service handles POSDbContext only.
///
/// Startup order (see App.xaml.cs):
///   Platform DB → Catalog DB → Inventory DB → Sales DB → POS DB → Application starts
///
/// Architecture reference: §36 (Local Database Migrations).
/// </summary>
public sealed class POSDatabaseInitializer(
    IServiceProvider serviceProvider,
    ILogger<POSDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing POS database schema...");

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<POSDbContext>();

            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
            var pendingList = pendingMigrations.ToList();

            if (pendingList.Count > 0)
            {
                logger.LogInformation(
                    "Applying {Count} pending POS migration(s): {Migrations}",
                    pendingList.Count,
                    string.Join(", ", pendingList));

                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("POS migrations applied successfully.");
            }
            else
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("POS database schema is up to date.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize POS database schema.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
