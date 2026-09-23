using Microsoft.Extensions.Logging;
using System.Windows;

namespace Client.Desktop;

/// <summary>
/// The application shell window.
///
/// Stage 2 scope:
/// - This window proves that DI, host, and WPF work together correctly.
/// - It contains no business logic, navigation, or module content.
/// - Business module UIs will be hosted here starting in Stage 5.
///
/// Services are injected via constructor injection.
/// Do NOT use the service locator pattern here or in derived windows.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(ILogger<MainWindow> logger)
    {
        _logger = logger;
        InitializeComponent();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _logger.LogInformation("Main window rendered. Application is ready.");
    }

    protected override void OnClosed(EventArgs e)
    {
        _logger.LogInformation("Main window closed. Application shutting down.");
        base.OnClosed(e);
    }
}