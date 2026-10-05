using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Payments.Infrastructure.Persistence;

namespace Payments.Infrastructure.Module;

/// <summary>
/// Hosted service that applies pending Payments EF Core migrations at startup (each module owns its migration runner).
/// Handles PaymentsDbContext only.
/// </summary>
public sealed class PaymentsDatabaseInitializer(
    IServiceProvider serviceProvider,
    ILogger<PaymentsDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Payments database schema...");

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();

            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending Payments migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Payments migrations applied successfully.");
            }
            else
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("Payments database schema is up to date.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Payments database schema.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
