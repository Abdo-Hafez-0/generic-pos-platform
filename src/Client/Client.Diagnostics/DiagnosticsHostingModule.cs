using Client.Diagnostics.Crash;
using Client.Diagnostics.Logging;
using Client.Diagnostics.Support;
using Client.Host.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Authorization;
using Platform.Infrastructure.Persistence;

namespace Client.Diagnostics;

/// <summary>Marker for architecture tests.</summary>
public sealed class ClientDiagnosticsAssemblyMarker;

public static class DiagnosticsServicesExtensions
{
    /// <summary>
    /// The log file (a logging provider next to the console and debug ones), the crash reporter and the support file (MISS-05). The folder is
    /// <c>Diagnostics:LogDirectory</c> or "Logs" next to the database.
    /// </summary>
    public static IServiceCollection AddClientDiagnostics(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new DiagnosticsConfiguration();
        configuration.GetSection(DiagnosticsConfiguration.SectionName).Bind(options);
        var database = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(database);
        var folders = new DiagnosticsFolders(string.IsNullOrWhiteSpace(options.LogDirectory)
            ? Path.Combine(Path.GetDirectoryName(database.ResolveDatabasePath())!, "Logs")
            : options.LogDirectory!);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(folders);
        services.AddSingleton(sp => new RollingLogWriter(folders, options, sp.GetRequiredService<TimeProvider>()));
        services.AddLogging(logging => logging.Services.AddSingleton<ILoggerProvider>(sp => new FileLoggerProvider(sp.GetRequiredService<RollingLogWriter>(), options)));
        services.AddSingleton(sp => new CrashReporter(sp.GetRequiredService<RollingLogWriter>(), sp.GetRequiredService<TimeProvider>(), sp));

        services.AddSingleton<SupportService>();
        services.AddSingleton<ICrashNoticeSource>(sp => sp.GetRequiredService<SupportService>());
        services.AddSingleton<ICapabilityProvider, SupportCapabilityProvider>();
        services.AddTransient<GetSupportOverviewQueryHandler>();
        services.AddTransient<MarkCrashReportsSeenCommandHandler>();
        services.AddTransient<SaveSupportFileCommandHandler>();
        return services;
    }
}

public sealed class DiagnosticsHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.AddClientDiagnostics(context.Configuration);
}
