using AdminPortal.Application;
using Cloud.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cloud.Infrastructure;

/// <summary>
/// Security facts about customer-facing API access go into the SAME append-only table as the vendor's administrative audit log
/// (adm_AuditLog), under an actor such as "license:&lt;id&gt;" or "anonymous", so the vendor reads one trail. Best effort: a failing write is
/// swallowed, because a log that cannot be written must not turn an answered request into an error.
/// </summary>
internal sealed class AdminAuditBackedSecurityLog(IAdminAuditLog log, TimeProvider timeProvider) : ICloudSecurityLog
{
    public async Task RecordAsync(string actor, string action, string entityType, string entityId, string summary, CancellationToken cancellationToken = default)
    {
        try
        {
            await log.AppendAsync(new AdminAuditEntry
            {
                Id = Guid.NewGuid(),
                At = timeProvider.GetUtcNow(),
                Actor = Truncate(actor, 100),
                Action = Truncate(action, 100),
                EntityType = Truncate(entityType, 100),
                EntityId = Truncate(entityId, 100),
                Summary = Truncate(summary, 500)
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best effort by contract
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

public static class CloudSecurityExtensions
{
    /// <summary>
    /// Registers the authentication throttle and the security log. Configuration (all optional; the throttle can be made stricter but never off):
    /// <c>Security:AuthThrottle:MaxFailures</c> (default 10, at least 3), <c>WindowMinutes</c> (10), <c>LockoutMinutes</c> (15).
    /// </summary>
    public static IServiceCollection AddCloudSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Security:AuthThrottle");
        var options = new AuthenticationThrottleOptions(
            section.GetValue("MaxFailures", AuthenticationThrottleOptions.Default.MaxFailures),
            TimeSpan.FromMinutes(section.GetValue("WindowMinutes", AuthenticationThrottleOptions.DefaultWindow.TotalMinutes)),
            TimeSpan.FromMinutes(section.GetValue("LockoutMinutes", AuthenticationThrottleOptions.DefaultLockout.TotalMinutes))).Normalized();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new AuthenticationThrottle(options, sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ICloudSecurityLog>(sp => sp.GetService<IAdminAuditLog>() is { } log
            ? new AdminAuditBackedSecurityLog(log, sp.GetRequiredService<TimeProvider>())
            : new NullCloudSecurityLog());
        return services;
    }
}
