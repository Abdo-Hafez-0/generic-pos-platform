using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Suppliers.Infrastructure.Persistence;

namespace Suppliers.Infrastructure.Module;

/// <summary>
/// Hosted service that applies pending Suppliers EF Core migrations at startup (each module owns its migration runner).
/// Handles SuppliersDbContext only.
/// </summary>
public sealed class SuppliersDatabaseInitializer(
    IServiceProvider serviceProvider,
    ILogger<SuppliersDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Suppliers database schema...");

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SuppliersDbContext>();

            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending Suppliers migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Suppliers migrations applied successfully.");
            }
            else
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("Suppliers database schema is up to date.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Suppliers database schema.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
