using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Client.Desktop.Resources;
using Client.Diagnostics.Support;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Client.Desktop.Screens.Support;

/// <summary>One crash report, with its texts in the display language.</summary>
public sealed record CrashReportRow(string WhenText, string Summary);

/// <summary>
/// Administration > Support (MISS-05): where the log files are, the crash reports kept, and "Save support file..." (a .zip of the logs
/// and crash reports, never the shop's data file, to send to the vendor). Opening the screen counts as having seen the crash reports, so
/// the shell's notice goes away. Everything needs support.export, checked by the handlers.
/// </summary>
public sealed class SupportViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private string _logFolder = string.Empty, _logFilesText = string.Empty;
    private bool _hasCrashes;

    public SupportViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        SaveCommand = Command<string>(path => SaveAsync(path!), path => !string.IsNullOrWhiteSpace(path));
    }

    /// <summary>Saves the support file to the path given as the parameter (the view asks where).</summary>
    public ICommand SaveCommand { get; }

    public string LogFolder { get => _logFolder; private set => Set(ref _logFolder, value); }
    public string LogFilesText { get => _logFilesText; private set => Set(ref _logFilesText, value); }

    public ObservableCollection<CrashReportRow> CrashReports { get; } = [];

    public bool HasCrashes
    {
        get => _hasCrashes;
        private set
        {
            if (Set(ref _hasCrashes, value)) Raise(nameof(HasNoCrashes));
        }
    }

    public bool HasNoCrashes => !HasCrashes;

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => LoadAsync(cancellationToken));

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var overview = await _runner.RunAsync((scope, ct) => scope.Get<GetSupportOverviewQueryHandler>().HandleAsync(new GetSupportOverviewQuery(), ct), cancellationToken);
        if (!Accept(overview)) return;
        var o = overview.Value;

        LogFolder = Ltr(o.LogFolder);
        LogFilesText = o.LogFileCount == 0
            ? SupportText.LogFilesNone
            : string.Format(CultureInfo.CurrentCulture, SupportText.LogFilesValue, o.LogFileCount, Ltr(Size(o.LogBytes)));
        CrashReports.Clear();
        foreach (var report in o.CrashReports)
            CrashReports.Add(new CrashReportRow(Ltr(report.At.ToString("g", CultureInfo.CurrentCulture)), Ltr(report.Summary)));
        HasCrashes = CrashReports.Count > 0;

        if (o.UnseenCrashes > 0)
            await _runner.RunAsync((scope, ct) => scope.Get<MarkCrashReportsSeenCommandHandler>().HandleAsync(new MarkCrashReportsSeenCommand(), ct), cancellationToken);
    }

    public async Task SaveAsync(string path)
    {
        var saved = await _runner.RunAsync((scope, ct) => scope.Get<SaveSupportFileCommandHandler>().HandleAsync(new SaveSupportFileCommand(path), ct));
        if (!Accept(saved)) return;
        StatusMessage = string.Format(CultureInfo.CurrentCulture, SupportText.Saved, Ltr(saved.Value.Path), saved.Value.LogFiles, saved.Value.CrashReports);
        await LoadAsync(CancellationToken.None);
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", bytes / (1024.0 * 1024.0))
        : string.Format(CultureInfo.CurrentCulture, "{0} KB", Math.Max(1, bytes / 1024));

    /// <summary>Paths, dates and the English error texts keep their order inside Arabic text (FIX-13c).</summary>
    private static string Ltr(string value) => "‎" + value + "‎";
}
