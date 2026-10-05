using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pricing.Infrastructure.Persistence;

namespace Pricing.Infrastructure.Module;

/// <summary>
/// Hosted service that applies pending Pricing EF Core migrations at startup (each module owns its migration runner).
/// Handles PricingDbContext only.
/// </summary>
public sealed class PricingDatabaseInitializer(
    IServiceProvider serviceProvider,
    ILogger<PricingDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Pricing database schema...");

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<PricingDbContext>();

            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending Pricing migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Pricing migrations applied successfully.");
            }
            else
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("Pricing database schema is up to date.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Pricing database schema.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
