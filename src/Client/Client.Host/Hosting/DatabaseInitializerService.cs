using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Infrastructure.Persistence;

namespace Client.Host.Hosting;

/// <summary>
/// An IHostedService that initializes the platform database during application startup.
///
/// This service runs when IHost.StartAsync() is called, which means database
/// initialization happens BEFORE the WPF main window is shown.
///
/// STARTUP ORDER (guaranteed by Microsoft.Extensions.Hosting):
///   1. IHostedServices are started in registration order.
///   2. DatabaseInitializerService starts first (registered first via AddPlatformServices).
///   3. Platform database is initialized.
///   4. Host.StartAsync() returns.
///   5. App.xaml.cs shows MainWindow.
///
/// If database initialization fails, the exception propagates through StartAsync()
/// and is handled by App.xaml.cs which shows an error dialog and exits cleanly.
///
/// SCOPE:
/// IHostedService runs in the root scope. The DatabaseInitializer is Scoped,
/// so this service creates an explicit IServiceScope for the initialization operation.
/// </summary>
internal sealed class DatabaseInitializerService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseInitializerService> _logger;

    public DatabaseInitializerService(
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseInitializerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting platform database initialization...");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync(cancellationToken);

        _logger.LogInformation("Platform database initialization completed.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Nothing to do on stop — SQLite connections are closed per-scope.
        return Task.CompletedTask;
    }
}
