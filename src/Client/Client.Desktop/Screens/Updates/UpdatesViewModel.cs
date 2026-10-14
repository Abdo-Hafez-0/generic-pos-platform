using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Client.Desktop.Resources;
using Client.Desktop.Shell;
using Client.Updater.Application;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using Updates.Contracts;

namespace Client.Desktop.Screens.Updates;

/// <summary>A part of the application (the core or a module) with its texts.</summary>
public sealed record UpdatePartRow(UpdatePart Part, string NameText, string InUseText, string ActiveText, string StateText);

/// <summary>An update the server offers, with its texts.</summary>
public sealed record FoundUpdateRow(UpdateInfo Info, string NameText, string VersionText, string SizeText);

/// <summary>
/// Administration > Updates (PKG-01b, user decision 2026-10-14): the versions in use and the ones made active, "Check for updates" and
/// install one, "Install from a file..." (a shop without internet gets the file from its vendor), "Go back to the previous version", and
/// "Restart now" (offered, never forced). Every package is verified end to end by the updater (signature first) whatever its source;
/// everything needs updates.manage, checked by the handlers.
/// </summary>
public sealed class UpdatesViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private readonly IApplicationRestarter _restarter;
    private UpdatePartRow? _selectedPart;
    private FoundUpdateRow? _selectedFound;
    private bool _startedByLauncher = true, _restartWouldHelp, _checked;

    public UpdatesViewModel(IUiActionRunner runner, IApplicationRestarter restarter)
    {
        _runner = runner;
        _restarter = restarter;
        CheckCommand = Command(CheckAsync);
        InstallFoundCommand = Command(InstallFoundAsync, () => SelectedFound is not null);
        InstallFromFileCommand = Command<string>(path => InstallAsync(path!), path => !string.IsNullOrWhiteSpace(path));
        GoBackCommand = Command(GoBackAsync, () => SelectedPart is { Part.CanGoBack: true });
        RestartCommand = Command(() => { _restarter.Restart(); return Task.CompletedTask; }, () => RestartWouldHelp);
    }

    public ICommand CheckCommand { get; }
    public ICommand InstallFoundCommand { get; }

    /// <summary>Installs the package file given as the parameter (the view asks which).</summary>
    public ICommand InstallFromFileCommand { get; }

    public ICommand GoBackCommand { get; }
    public ICommand RestartCommand { get; }

    public ObservableCollection<UpdatePartRow> Parts { get; } = [];
    public ObservableCollection<FoundUpdateRow> Found { get; } = [];

    public UpdatePartRow? SelectedPart { get => _selectedPart; set => Set(ref _selectedPart, value); }
    public FoundUpdateRow? SelectedFound { get => _selectedFound; set => Set(ref _selectedFound, value); }

    /// <summary>False when this copy was not started by GenericPOS.exe: installed updates are then not used (the screen says so).</summary>
    public bool StartedByLauncher
    {
        get => _startedByLauncher;
        private set
        {
            if (Set(ref _startedByLauncher, value)) Raise(nameof(NotStartedByLauncher));
        }
    }

    public bool NotStartedByLauncher => !StartedByLauncher;

    public bool RestartWouldHelp { get => _restartWouldHelp; private set => Set(ref _restartWouldHelp, value); }

    public bool HasFound => Found.Count > 0;
    public bool HasNoneFound => _checked && Found.Count == 0;

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => LoadAsync(cancellationToken));

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var overview = await _runner.RunAsync((scope, ct) => scope.Get<GetUpdateOverviewQueryHandler>().HandleAsync(new GetUpdateOverviewQuery(), ct), cancellationToken);
        if (!Accept(overview)) return;

        var o = overview.Value;
        var selected = SelectedPart?.Part.TargetId;
        Parts.Clear();
        foreach (var part in o.Parts)
            Parts.Add(new UpdatePartRow(part, NameOf(part.TargetId, part.Name), Ltr(part.Running ?? "-"), part.Active is null ? UpdatesText.AsInstalled : Ltr(part.Active), StateOf(part)));
        SelectedPart = Parts.FirstOrDefault(p => p.Part.TargetId == selected);
        StartedByLauncher = o.StartedByLauncher;
        RestartWouldHelp = o.RestartWouldHelp;
    }

    private async Task CheckAsync()
    {
        var found = await _runner.RunAsync((scope, ct) => scope.Get<CheckForUpdatesQueryHandler>().HandleAsync(new CheckForUpdatesQuery(), ct));
        if (!Accept(found)) return;

        Found.Clear();
        foreach (var update in found.Value.OrderBy(u => u.TargetId == PackageManifest.CoreTargetId ? 0 : 1).ThenBy(u => u.TargetId, StringComparer.Ordinal))
            Found.Add(new FoundUpdateRow(update, NameOf(update.TargetId, null), Ltr(update.Version), Ltr(Size(update.SizeBytes))));
        SelectedFound = Found.FirstOrDefault();
        _checked = true;
        Raise(nameof(HasFound));
        Raise(nameof(HasNoneFound));
        StatusMessage = Found.Count == 0 ? UpdatesText.NoneFound : string.Format(CultureInfo.CurrentCulture, UpdatesText.FoundCount, Found.Count);
    }

    private async Task InstallFoundAsync()
    {
        var update = SelectedFound!.Info;
        var downloaded = await _runner.RunAsync((scope, ct) => scope.Get<DownloadUpdateCommandHandler>().HandleAsync(new DownloadUpdateCommand(update), ct));
        if (!Accept(downloaded)) return;
        if (await InstallCoreAsync(downloaded.Value))
        {
            Found.Remove(SelectedFound!);
            SelectedFound = Found.FirstOrDefault();
            Raise(nameof(HasFound));
        }
    }

    public async Task InstallAsync(string path) => await InstallCoreAsync(path);

    private async Task<bool> InstallCoreAsync(string packagePath)
    {
        var installed = await _runner.RunAsync((scope, ct) => scope.Get<InstallUpdateCommandHandler>().HandleAsync(new InstallUpdateCommand(packagePath), ct));
        if (!Accept(installed)) return false;

        var journal = installed.Value;
        await LoadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, StartedByLauncher ? UpdatesText.Installed : UpdatesText.InstalledNeedsLauncher,
            NameOf(journal.TargetId, null), Ltr(journal.Version));
        return true;
    }

    private async Task GoBackAsync()
    {
        var part = SelectedPart!.Part;
        var name = SelectedPart.NameText;
        var rolledBack = await _runner.RunAsync((scope, ct) => scope.Get<RollbackUpdateCommandHandler>().HandleAsync(new RollbackUpdateCommand(part.TargetId), ct));
        if (!Accept(rolledBack)) return;

        await LoadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, UpdatesText.WentBack, name);
    }

    private string NameOf(string targetId, string? name)
        => targetId == PackageManifest.CoreTargetId ? UpdatesText.Application
            : !string.IsNullOrEmpty(name) && name != targetId ? name
            : Parts.FirstOrDefault(p => p.Part.TargetId == targetId)?.NameText ?? targetId;

    private static string StateOf(UpdatePart part) => part.State switch
    {
        UpdatePartState.InUse => UpdatesText.StateInUse,
        UpdatePartState.WaitsForRestart => UpdatesText.StateAfterRestart,
        UpdatePartState.WaitsForLauncher => UpdatesText.StateNeedsLauncher,
        UpdatePartState.NotStarted => string.Format(CultureInfo.CurrentCulture, UpdatesText.StateNotStarted, Ltr(part.Reason ?? string.Empty)),
        UpdatePartState.NeedsAttention => string.Format(CultureInfo.CurrentCulture, UpdatesText.StateNeedsVendor, Ltr(part.Reason ?? string.Empty)),
        _ => string.Empty
    };

    private static string Size(long bytes) => bytes >= 1024 * 1024
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", bytes / (1024.0 * 1024.0))
        : string.Format(CultureInfo.CurrentCulture, "{0} KB", Math.Max(1, bytes / 1024));

    /// <summary>Versions, sizes and the English texts of the updater keep their order inside Arabic text (FIX-13c).</summary>
    private static string Ltr(string value) => "‎" + value + "‎";
}
