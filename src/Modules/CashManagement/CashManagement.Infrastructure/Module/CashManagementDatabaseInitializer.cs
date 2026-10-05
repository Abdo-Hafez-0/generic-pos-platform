using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CashManagement.Infrastructure.Persistence;

namespace CashManagement.Infrastructure.Module;

/// <summary>
/// Hosted service that applies pending CashManagement EF Core migrations at startup (each module owns its migration runner).
/// Handles CashManagementDbContext only.
/// </summary>
public sealed class CashManagementDatabaseInitializer(
    IServiceProvider serviceProvider,
    ILogger<CashManagementDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing CashManagement database schema...");

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CashManagementDbContext>();

            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending CashManagement migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("CashManagement migrations applied successfully.");
            }
            else
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("CashManagement database schema is up to date.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize CashManagement database schema.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
