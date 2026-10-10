using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Desktop.Resources;
using Client.Desktop.Shell;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Client.Desktop.Screens.Backup;

/// <summary>One backup in the list, with its texts in the display language.</summary>
public sealed record BackupRow(BackupRecord Record, string MadeText, string WhereText, string SizeText, string HowText, string CheckText);

/// <summary>
/// The backup screen (MISS-04d): the state of the backups in plain words, "Back up now", the list (check, restore, delete), restoring a
/// file from another PC, and the settings (folder, how many to keep, the daily time).
///
/// Every decision stays in Client.Backup: each action goes through its handler (which checks backup.create / restore / delete /
/// configure) in its own scope. Parts the signed-in person may not use are hidden. Restoring and deleting ask first, in a sentence that
/// says what will happen; a confirmed restore happens at the next start, so the screen offers to restart at once.
/// </summary>
public sealed class BackupViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private readonly IApplicationRestarter _restarter;

    private bool _canMake, _canRestore, _canDelete, _canConfigure;
    private string _lastBackupText = string.Empty, _folderText = string.Empty, _scheduleText = string.Empty, _lastRestoreText = string.Empty;
    private BackupRow? _selected;
    private RestorePreparation? _prepared;
    private string _restoreQuestion = string.Empty;
    private PendingRestore? _pending;
    private BackupRow? _deleting;
    private string _deleteQuestion = string.Empty;
    private string? _folder;
    private string _keepText = BackupSettings.DefaultKeepLocal.ToString(CultureInfo.CurrentCulture);
    private bool _scheduleEnabled = true;
    private string _dailyAtText = "23:00";

    public BackupViewModel(IUiActionRunner runner, IApplicationRestarter restarter)
    {
        _runner = runner;
        _restarter = restarter;
        BackUpNowCommand = Command(BackUpNowAsync, () => CanMake);
        CheckCommand = Command(CheckAsync, () => CanMake && Selected is not null);
        RestoreCommand = Command(() => PrepareAsync(Selected!), () => CanRestore && Selected is not null && Pending is null);
        RestoreFromFileCommand = Command<string>(path => PrepareFromFileAsync(path!), path => CanRestore && !string.IsNullOrWhiteSpace(path) && Pending is null);
        ConfirmRestoreCommand = Command(ConfirmRestoreAsync, () => IsRestorePrepared);
        CancelRestoreCommand = Command(CancelRestoreAsync, () => IsRestorePrepared || IsRestorePending);
        RestartNowCommand = Command(RestartAsync, () => IsRestorePending);
        DeleteCommand = Command(AskDeleteAsync, () => CanDelete && Selected is not null);
        ConfirmDeleteCommand = Command(DeleteAsync, () => IsDeleteAsked);
        KeepCommand = Command(() => { AskDelete(null); return Task.CompletedTask; }, () => IsDeleteAsked);
        SaveSettingsCommand = Command(SaveSettingsAsync, () => CanConfigure);
    }

    public ICommand BackUpNowCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand RestoreFromFileCommand { get; }
    public ICommand ConfirmRestoreCommand { get; }
    public ICommand CancelRestoreCommand { get; }
    public ICommand RestartNowCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand KeepCommand { get; }
    public ICommand SaveSettingsCommand { get; }

    // ------------------------------------------------------------------ what the person may do

    public bool CanMake { get => _canMake; private set => Set(ref _canMake, value); }
    public bool CanRestore { get => _canRestore; private set => Set(ref _canRestore, value); }
    public bool CanDelete { get => _canDelete; private set => Set(ref _canDelete, value); }
    public bool CanConfigure { get => _canConfigure; private set => Set(ref _canConfigure, value); }

    // ------------------------------------------------------------------ state

    public string LastBackupText { get => _lastBackupText; private set => Set(ref _lastBackupText, value); }
    public string FolderText { get => _folderText; private set => Set(ref _folderText, value); }
    public string ScheduleText { get => _scheduleText; private set => Set(ref _scheduleText, value); }

    /// <summary>What happened to the last restore (empty when there was none).</summary>
    public string LastRestoreText { get => _lastRestoreText; private set => Set(ref _lastRestoreText, value); }

    public ObservableCollection<BackupRow> Backups { get; } = [];

    public BackupRow? Selected { get => _selected; set => Set(ref _selected, value); }

    // ------------------------------------------------------------------ restore

    public bool IsRestorePrepared => _prepared is not null;

    /// <summary>The sentence that says what a restore replaces (shown before it is confirmed).</summary>
    public string RestoreQuestion { get => _restoreQuestion; private set => Set(ref _restoreQuestion, value); }

    public PendingRestore? Pending
    {
        get => _pending;
        private set
        {
            if (Set(ref _pending, value))
            {
                Raise(nameof(IsRestorePending));
                Raise(nameof(PendingText));
            }
        }
    }

    public bool IsRestorePending => Pending is not null;

    public string PendingText => Pending is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, BackupText.RestorePending, Pending.SourceFileName);

    // ------------------------------------------------------------------ delete

    public bool IsDeleteAsked => _deleting is not null;

    public string DeleteQuestion { get => _deleteQuestion; private set => Set(ref _deleteQuestion, value); }

    // ------------------------------------------------------------------ settings

    public string? Folder { get => _folder; set => Set(ref _folder, value); }
    public string KeepText { get => _keepText; set => Set(ref _keepText, value); }
    public bool ScheduleEnabled { get => _scheduleEnabled; set => Set(ref _scheduleEnabled, value); }
    public string DailyAtText { get => _dailyAtText; set => Set(ref _dailyAtText, value); }

    // ------------------------------------------------------------------ loading

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => LoadAsync(cancellationToken));

    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var allowed = await _runner.QueryAsync(async (scope, ct) =>
        {
            var authorization = scope.Get<IAuthorizationService>();
            return (Make: await authorization.IsAllowedAsync(BackupCapabilities.Create, ct),
                    Restore: await authorization.IsAllowedAsync(BackupCapabilities.Restore, ct),
                    Delete: await authorization.IsAllowedAsync(BackupCapabilities.Delete, ct),
                    Configure: await authorization.IsAllowedAsync(BackupCapabilities.Configure, ct));
        }, cancellationToken);
        if (!Accept(allowed)) return;
        (CanMake, CanRestore, CanDelete, CanConfigure) = allowed.Value;
        if (!CanMake) return;   // every reading needs backup.create

        var settings = await _runner.RunAsync((scope, ct) => scope.Get<GetBackupSettingsQueryHandler>().HandleAsync(new GetBackupSettingsQuery(), ct), cancellationToken);
        if (!Accept(settings)) return;
        ShowSettings(settings.Value);

        await LoadHistoryAsync(cancellationToken);

        var status = await _runner.RunAsync((scope, ct) => scope.Get<GetRestoreStatusQueryHandler>().HandleAsync(new GetRestoreStatusQuery(), ct), cancellationToken);
        if (!Accept(status)) return;
        Pending = status.Value.Pending;
        LastRestoreText = status.Value.LastOutcome is { } outcome
            ? string.Format(CultureInfo.CurrentCulture, BackupText.LastRestore, Ltr(outcome.CompletedAt.ToString("g", CultureInfo.CurrentCulture)), Ltr(outcome.Message))
            : string.Empty;
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        var history = await _runner.RunAsync((scope, ct) => scope.Get<GetBackupHistoryQueryHandler>().HandleAsync(new GetBackupHistoryQuery(), ct), cancellationToken);
        if (!Accept(history)) return;

        var selectedId = Selected?.Record.Id;
        Backups.Clear();
        foreach (var record in history.Value) Backups.Add(Row(record));
        Selected = Backups.FirstOrDefault(r => r.Record.Id == selectedId);

        var last = history.Value.FirstOrDefault(r => r.Destination == BackupDestinations.Local);
        LastBackupText = last is null
            ? BackupText.NoBackupYet
            : string.Format(CultureInfo.CurrentCulture, BackupText.LastBackupValue, Ltr(last.CreatedAt.ToString("g", CultureInfo.CurrentCulture)), Ltr(last.FileName));
    }

    private void ShowSettings(BackupSettings settings)
    {
        FolderText = settings.LocalFolder ?? BackupText.NoFolder;
        ScheduleText = settings.ScheduleEnabled && settings.LocalFolder is not null
            ? string.Format(CultureInfo.CurrentCulture, BackupText.ScheduleAt, settings.ScheduledTime().ToString("HH:mm", CultureInfo.InvariantCulture))
            : BackupText.ScheduleOff;
        Folder = settings.LocalFolder;
        KeepText = settings.KeepLocal.ToString(CultureInfo.CurrentCulture);
        ScheduleEnabled = settings.ScheduleEnabled;
        DailyAtText = settings.ScheduledTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Dates, sizes, file names and the English business sentences keep their left-to-right order inside Arabic text (FIX-13c: WPF ignores
    /// the embedding characters; left-to-right marks around the value work).
    /// </summary>
    private static string Ltr(string value) => "\u200E" + value + "\u200E";

    private static BackupRow Row(BackupRecord record) => new(
        record,
        Ltr(record.CreatedAt.ToString("g", CultureInfo.CurrentCulture)),
        record.Destination == BackupDestinations.BeforeRestore ? BackupText.WhereBeforeRestore : BackupText.WhereLocal,
        record.SizeBytes >= 1024 * 1024
            ? Ltr(string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", record.SizeBytes / (1024.0 * 1024.0)))
            : Ltr(string.Format(CultureInfo.CurrentCulture, "{0} KB", Math.Max(1, record.SizeBytes / 1024))),
        record.Destination == BackupDestinations.BeforeRestore ? "-" : record.Origin == BackupOrigin.Scheduled ? BackupText.OriginScheduled : BackupText.OriginManual,
        record.LastVerifyPassed switch { true => BackupText.CheckPassed, false => BackupText.CheckFailed, null => BackupText.NotChecked });

    // ------------------------------------------------------------------ back up, check

    private async Task BackUpNowAsync()
    {
        var made = await _runner.RunAsync((scope, ct) => scope.Get<CreateBackupCommandHandler>().HandleAsync(new CreateBackupCommand(), ct));
        await LoadHistoryAsync();
        if (!Accept(made)) return;
        StatusMessage = string.Format(CultureInfo.CurrentCulture, BackupText.BackupMade, made.Value.FileName);
    }

    private async Task CheckAsync()
    {
        var id = Selected!.Record.Id;
        var checkedBackup = await _runner.RunAsync((scope, ct) => scope.Get<VerifyBackupCommandHandler>().HandleAsync(new VerifyBackupCommand(id), ct));
        await LoadHistoryAsync();
        if (!Accept(checkedBackup)) return;
        StatusMessage = string.Format(CultureInfo.CurrentCulture, BackupText.CheckedGood, checkedBackup.Value.FileName);
    }

    // ------------------------------------------------------------------ restore

    private Task PrepareAsync(BackupRow row)
        => ShowPreparedAsync(_runner.RunAsync((scope, ct) => scope.Get<PrepareRestoreCommandHandler>().HandleAsync(new PrepareRestoreCommand(row.Record.Id), ct)));

    /// <summary>A backup file chosen from disk (another PC, a USB drive); the view's file dialog supplies the path.</summary>
    public Task PrepareFromFileAsync(string path)
        => ShowPreparedAsync(_runner.RunAsync((scope, ct) => scope.Get<PrepareRestoreFromFileCommandHandler>().HandleAsync(new PrepareRestoreFromFileCommand(path), ct)));

    private async Task ShowPreparedAsync(Task<Platform.Core.Results.Result<RestorePreparation>> preparing)
    {
        SetPrepared(null);
        var prepared = await preparing;
        if (!Accept(prepared)) return;

        SetPrepared(prepared.Value);
        StatusMessage = null;
    }

    private void SetPrepared(RestorePreparation? prepared)
    {
        _prepared = prepared;
        RestoreQuestion = prepared is null
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, BackupText.RestoreQuestion, Ltr(prepared.BackupCreatedAt.ToString("g", CultureInfo.CurrentCulture)), Ltr(prepared.FileName));
        Raise(nameof(IsRestorePrepared));
    }

    private async Task ConfirmRestoreAsync()
    {
        var id = _prepared!.Id;
        var confirmed = await _runner.RunAsync((scope, ct) => scope.Get<ConfirmRestoreCommandHandler>().HandleAsync(new ConfirmRestoreCommand(id), ct));
        SetPrepared(null);
        if (!Accept(confirmed)) return;
        Pending = confirmed.Value;
    }

    private async Task CancelRestoreAsync()
    {
        var cancelled = await _runner.RunAsync((scope, ct) => scope.Get<CancelRestoreCommandHandler>().HandleAsync(new CancelRestoreCommand(), ct));
        SetPrepared(null);
        if (!Accept(cancelled)) return;
        Pending = null;
        StatusMessage = BackupText.RestoreCancelled;
    }

    private Task RestartAsync()
    {
        _restarter.Restart();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ delete

    private Task AskDeleteAsync()
    {
        AskDelete(Selected);
        return Task.CompletedTask;
    }

    private void AskDelete(BackupRow? row)
    {
        _deleting = row;
        DeleteQuestion = row is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, BackupText.DeleteQuestion, row.Record.FileName);
        Raise(nameof(IsDeleteAsked));
    }

    private async Task DeleteAsync()
    {
        var row = _deleting!;
        AskDelete(null);
        var deleted = await _runner.RunAsync((scope, ct) => scope.Get<DeleteBackupCommandHandler>().HandleAsync(new DeleteBackupCommand(row.Record.Id), ct));
        await LoadHistoryAsync();
        if (!Accept(deleted)) return;
        StatusMessage = string.Format(CultureInfo.CurrentCulture, BackupText.Deleted, row.Record.FileName);
    }

    // ------------------------------------------------------------------ settings

    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(KeepText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var keep))
        {
            ErrorMessage = BackupText.InvalidKeep;
            return;
        }

        if (!TimeOnly.TryParseExact(DailyAtText?.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dailyAt))
        {
            ErrorMessage = BackupText.InvalidTime;
            return;
        }

        var command = new UpdateBackupSettingsCommand(Folder, keep, ScheduleEnabled, dailyAt);
        var saved = await _runner.RunAsync((scope, ct) => scope.Get<UpdateBackupSettingsCommandHandler>().HandleAsync(command, ct));
        if (!Accept(saved)) return;

        ShowSettings(saved.Value);
        StatusMessage = BackupText.Saved;
    }
}
