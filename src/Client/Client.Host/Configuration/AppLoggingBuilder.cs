using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Client.Host.Configuration;

/// <summary>
/// Configures the application logging pipeline.
///
/// Stage 2 logging strategy:
/// - Console logging (for development)
/// - Debug logging (for Visual Studio Output window)
/// - Structured minimum log level from configuration
///
/// NOT configured here (these belong to later stages):
/// - File logging       — Stage 3+ (once infrastructure folder is determined by DB setup)
/// - Remote telemetry   — will NOT be implemented; logs remain local per architecture
/// - Event log          — optional, can be added in a later hardening stage
///
/// Modules can configure additional log providers during their service registration (Stage 5+).
/// </summary>
internal static class AppLoggingBuilder
{
    internal static void Configure(HostBuilderContext context, ILoggingBuilder logging)
    {
        logging.ClearProviders();

        logging
            .AddConsole()
            .AddDebug()
            .SetMinimumLevel(LogLevel.Debug);

        // Allow configuration to override log levels:
        //   "Logging": { "LogLevel": { "Default": "Information", "Client": "Debug" } }
        logging.AddConfiguration(context.Configuration.GetSection("Logging"));
    }
}
