using System.Windows.Threading;
using Client.Desktop.Resources;
using Client.Diagnostics.Crash;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Client.Desktop.Shell;

/// <summary>
/// What happens when something fails that nobody handled (MISS-05):
///   - on the screen's thread: a crash report is written, a plain message says the application must close, and it closes (its state is
///     unknown, so it does not carry on);
///   - on a background thread: the report is written (the process ends anyway);
///   - a background task whose failure nobody observed: written to the log as an error, the application carries on;
///   - a failure while starting: the report is written next to the existing startup message.
/// Before the host exists the reports go to %LOCALAPPDATA%\GenericPOS\Logs; afterwards to the data folder's Logs.
/// </summary>
public sealed class CrashHandling(Action<string> showMessage, Action<int> shutdown)
{
    private CrashReporter? _reporter;
    private ILogger? _logger;
    private int _closing;

    private CrashReporter Reporter => _reporter ??= CrashReporter.Fallback();

    /// <summary>Once the host is built: reports go next to the database and include the modules.</summary>
    public void Use(IServiceProvider services)
    {
        _reporter = services.GetService<CrashReporter>() ?? _reporter;
        _logger = services.GetService<ILogger<CrashHandling>>();
    }

    public void Attach(Dispatcher dispatcher)
    {
        dispatcher.UnhandledException += (_, e) => e.Handled = OnScreenFailure(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnBackgroundFailure(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            OnUnobservedTask(e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Returns true: the failure is dealt with (reported, the person told, the application closing).</summary>
    public bool OnScreenFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1) return true;   // a second failure while closing: already reported
        Reporter.Report(exception, CrashOrigin.Screen);
        try
        {
            showMessage(ShellText.CrashMessage);
        }
        finally
        {
            shutdown(1);
        }

        return true;
    }

    public void OnBackgroundFailure(Exception? exception)
    {
        if (exception is not null) Reporter.Report(exception, CrashOrigin.Background);
    }

    public void OnUnobservedTask(Exception exception)
        => _logger?.LogError(exception, "A background task failed and nothing waited for it; the application carries on.");

    public void OnStartupFailure(Exception exception) => Reporter.Report(exception, CrashOrigin.Startup);
}
