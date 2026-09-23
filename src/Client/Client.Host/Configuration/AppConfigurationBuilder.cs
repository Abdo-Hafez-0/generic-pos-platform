using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Client.Host.Configuration;

/// <summary>
/// Configures the application configuration sources.
///
/// Configuration priority (highest to lowest):
///   1. Environment variables (for dev/CI overrides)
///   2. appsettings.{Environment}.json  (e.g., appsettings.Development.json)
///   3. appsettings.json  (base configuration, committed to source control)
///
/// Design notes:
/// - Business module configuration is NOT set up here.
///   Modules register their own configuration sections when introduced (Stage 5+).
/// - No cloud configuration or remote settings sources are introduced here.
///   Cloud communication belongs to Stage 6 (Licensing) and Stage 7 (Updater).
/// - The configuration is intentionally minimal for Stage 2.
/// </summary>
internal static class AppConfigurationBuilder
{
    internal static void Configure(HostBuilderContext context, IConfigurationBuilder config)
    {
        var env = context.HostingEnvironment;

        config
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "GENERICPOS_");
    }
}
