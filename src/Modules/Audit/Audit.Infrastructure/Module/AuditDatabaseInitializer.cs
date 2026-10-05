using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Audit.Infrastructure.Persistence;

namespace Audit.Infrastructure.Module;

/// <summary>
/// Hosted service that applies pending Audit EF Core migrations at startup (each module owns its migration runner).
/// Handles AuditDbContext only.
/// </summary>
public sealed class AuditDatabaseInitializer(
    IServiceProvider serviceProvider,
    ILogger<AuditDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing Audit database schema...");

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

            var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending Audit migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await dbContext.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Audit migrations applied successfully.");
            }
            else
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("Audit database schema is up to date.");
            }

            // Security events raised before the audit tables existed (licensing and the updater start first) are written now.
            var listener = scope.ServiceProvider.GetService<Audit.Infrastructure.Services.AuditSecurityEventListener>();
            if (listener is not null) await listener.DrainAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize Audit database schema.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
