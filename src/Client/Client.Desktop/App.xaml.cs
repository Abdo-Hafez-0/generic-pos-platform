using Client.Host.Hosting;
using Client.Updater.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Presentation.Localization;
using System.Windows;

namespace Client.Desktop;

/// <summary>
/// Application entry point and composition root for the WPF desktop shell.
///
/// Startup sequence:
///   1. WPF Application.OnStartup fires
///   2. Create the application host via ApplicationHostBuilder
///   3. Register the hosting modules of DesktopComposition
///   4. Build the host
///   5. Start the host asynchronously (DI, logging, config, services are initialized)
///   6. Show the sign-in window (Stage 11: first-run setup / sign-in / password change); closing it exits.
///      Once it has rendered, the start was healthy (FIX-03): activated updates that really run are confirmed
///   7. Resolve the main window from DI and show it
///   8. On exit: stop the host gracefully
///
/// IMPORTANT rules for this class:
/// - Do NOT put business logic here.
/// - Do NOT access databases here.
/// - Do NOT query business data here.
/// - This class only creates the host and shows the main window.
/// - The main window itself must also contain no business logic.
/// </summary>
public partial class App : Application
{
    private IApplicationHost? _applicationHost;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Build the application host from the desktop composition (DesktopComposition: platform, client components, every module).
            var builder = ApplicationHostBuilder.Create();
            foreach (var module in DesktopComposition.HostingModules())
                builder.WithModule(module);
            _applicationHost = builder.Build();

            // Start all hosted services (logging, configuration, module discovery, etc.)
            await _applicationHost.StartAsync();

            // Log successful startup.
            var logger = _applicationHost.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation(
                "GenericPOS application started. Environment: {Environment}",
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production");

            // Stage 11: nobody reaches the shell without signing in. The start screen handles first-run setup (create the first
            // administrator), sign-in and the forced change of a temporary password, in the installation's language (Ui:Culture, FIX-01a;
            // an unknown value falls back to English). Closing it exits the application.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var session = _applicationHost.Services.GetRequiredService<DesktopSession>();
            UiCulture.ApplyFormatting(session.InstallationLanguage);   // numbers and dates: the installation's, whatever the screen language
            logger.LogInformation("Installation language: {Culture}.", UiCulture.Resolve(session.InstallationLanguage).Name);
            if (!session.ShowSignIn(signIn => signIn.ContentRendered += (_, _) => _ = ConfirmHealthyStartAsync(_applicationHost.Services, logger)))
            {
                logger.LogInformation("Nobody signed in; the application exits.");
                Shutdown(exitCode: 0);
                return;
            }

            // FIX-13b: the shell window (resolved from DI in DesktopSession) in the signed-in user's own language
            await session.OpenShellAsync();
        }
        catch (Exception ex)
        {
            // If host startup fails, show a plain statement and exit cleanly. The exception itself (database text, paths) goes to the log only.
            try
            {
                _applicationHost?.Services.GetService<ILogger<App>>()?.LogCritical(ex, "The application failed to start.");
            }
            catch (Exception)
            {
                // logging is best effort here: the host may be half started
            }

            System.Diagnostics.Debug.WriteLine(ex);
            MessageBox.Show(
                StartupFailure.Describe(ex),
                "Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(exitCode: 1);
        }
    }

    /// <summary>
    /// FIX-03: the host started (every hosted service, migrations, the fail-fast module lifecycle) and the start screen is shown - a
    /// healthy start, without waiting for anyone to sign in. Never fails the application.
    /// </summary>
    private static async Task ConfirmHealthyStartAsync(IServiceProvider services, ILogger logger)
    {
        try
        {
            if (services.GetService<StartupHealthConfirmation>() is not { } confirmation) return;
            foreach (var target in await confirmation.ConfirmAsync())
                logger.LogInformation("Healthy start confirmed for the update of {Target}.", target);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Confirming the healthy start failed; the application continues.");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_applicationHost is not null)
        {
            try
            {
                await _applicationHost.StopAsync();
            }
            catch (Exception ex)
            {
                // Log shutdown errors to debug output since logging may already be torn down.
                System.Diagnostics.Debug.WriteLine($"Error during host shutdown: {ex.Message}");
            }
        }

        base.OnExit(e);
    }
}
