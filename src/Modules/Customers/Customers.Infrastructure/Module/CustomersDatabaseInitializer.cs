using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Customers.Infrastructure.Persistence;

namespace Customers.Infrastructure.Module;

/// <summary>
/// Hosted service that applies pending Customers EF Core migrations at startup (each module owns its migration runner).
/// Handles CustomersDbContext only.
/// </summary>
public sealed class CustomersDatabaseInitializer(
    IServiceProvider serviceProvider,
    ILogger<CustomersDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Customers database schema...");

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();

            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending Customers migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Customers migrations applied successfully.");
            }
            else
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("Customers database schema is up to date.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Customers database schema.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
