using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Desktop.Resources;
using Client.Desktop.Shell;
using CloudBackup.Application;
using CloudBackup.Contracts;
using CloudBackup.Domain;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Client.Desktop.Screens.CloudBackup;

/// <summary>One backup the cloud keeps, with its texts in the display language.</summary>
public sealed record CloudBackupRow(CloudBackupItem Item, string MadeText, string NameText, string SizeText);

/// <summary>
/// The cloud backup screen (MISS-04e3, design sections 5, 8 and 10):
///   - setting up (backup.cloud.manage, needs the cloud-backup license): the state in plain words, the recovery code - shown ONCE, then two
///     of its groups typed back - and the vendor's backup access key;
///   - restoring (backup.cloud.restore, no license needed): the backups the cloud keeps; the chosen one is downloaded and opened with this
///     PC's key or the typed recovery code, then handed to the LOCAL restore (backup.restore): the same question, confirmation and restart
///     as on the Backup screen. The plain copy is discarded as soon as the local restore has taken its own.
/// Every action goes through its handler in its own scope; parts the person may not use are hidden.
/// </summary>
public sealed class CloudBackupViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private readonly IApplicationRestarter _restarter;

    private bool _canManage, _canRestore;
    private string _stateText = string.Empty, _codeStateText = string.Empty, _accessStateText = string.Empty, _waitingText = string.Empty;
    private string _lastUploadText = string.Empty, _lastFailureText = string.Empty;
    private bool _hasCode;
    private RecoveryCodeSetup? _setup;
    private string _firstGroup = string.Empty, _secondGroup = string.Empty, _accessKeyInput = string.Empty;
    private CloudBackupRow? _selected;
    private bool _isCodeNeeded;
    private string _recoveryCodeInput = string.Empty;
    private RestorePreparation? _prepared;
    private string _restoreQuestion = string.Empty;
    private PendingRestore? _pending;

    public CloudBackupViewModel(IUiActionRunner runner, IApplicationRestarter restarter)
    {
        _runner = runner;
        _restarter = restarter;
        MakeCodeCommand = Command(MakeCodeAsync, () => CanManage && !IsCodeShown);
        ConfirmCodeCommand = Command(ConfirmCodeAsync, () => IsCodeShown && !string.IsNullOrWhiteSpace(FirstGroup) && !string.IsNullOrWhiteSpace(SecondGroup));
        CancelCodeCommand = Command(() => { ShowSetup(null); return Task.CompletedTask; }, () => IsCodeShown);
        SaveAccessCommand = Command(SaveAccessAsync, () => CanManage && !string.IsNullOrWhiteSpace(AccessKeyInput));
        RemoveAccessCommand = Command(RemoveAccessAsync, () => CanManage);
        RefreshCloudCommand = Command(LoadCloudAsync, () => CanRestore);
        RestoreCommand = Command(RestoreAsync, () => CanRestore && Selected is not null && Pending is null && !IsRestorePrepared);
        ConfirmRestoreCommand = Command(ConfirmRestoreAsync, () => IsRestorePrepared);
        CancelRestoreCommand = Command(CancelRestoreAsync, () => IsRestorePrepared || IsRestorePending);
        RestartNowCommand = Command(() => { _restarter.Restart(); return Task.CompletedTask; }, () => IsRestorePending);
    }

    public ICommand MakeCodeCommand { get; }
    public ICommand ConfirmCodeCommand { get; }
    public ICommand CancelCodeCommand { get; }
    public ICommand SaveAccessCommand { get; }
    public ICommand RemoveAccessCommand { get; }
    public ICommand RefreshCloudCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ConfirmRestoreCommand { get; }
    public ICommand CancelRestoreCommand { get; }
    public ICommand RestartNowCommand { get; }

    public bool CanManage { get => _canManage; private set => Set(ref _canManage, value); }
    public bool CanRestore { get => _canRestore; private set => Set(ref _canRestore, value); }

    // ------------------------------------------------------------------ state

    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string CodeStateText { get => _codeStateText; private set => Set(ref _codeStateText, value); }
    public string AccessStateText { get => _accessStateText; private set => Set(ref _accessStateText, value); }
    public string WaitingText { get => _waitingText; private set => Set(ref _waitingText, value); }
    public string LastUploadText { get => _lastUploadText; private set => Set(ref _lastUploadText, value); }
    public string LastFailureText { get => _lastFailureText; private set => Set(ref _lastFailureText, value); }

    /// <summary>True once a recovery code is in use (the button then says "Make a new recovery code").</summary>
    public bool HasCode { get => _hasCode; private set { if (Set(ref _hasCode, value)) Raise(nameof(MakeCodeText)); } }

    public string MakeCodeText => HasCode ? CloudBackupText.MakeNewCode : CloudBackupText.MakeCode;

    // ------------------------------------------------------------------ recovery code (shown once)

    public bool IsCodeShown => _setup is not null;

    /// <summary>The code, while it is being set up. Never kept anywhere else; gone once confirmed or cancelled.</summary>
    public string CodeText => _setup?.Code ?? string.Empty;

    public string FirstGroupLabel => _setup is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, CloudBackupText.TypeGroup, _setup.GroupsToConfirm[0]);
    public string SecondGroupLabel => _setup is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, CloudBackupText.TypeGroup, _setup.GroupsToConfirm[1]);
    public string FirstGroup { get => _firstGroup; set => Set(ref _firstGroup, value); }
    public string SecondGroup { get => _secondGroup; set => Set(ref _secondGroup, value); }

    // ------------------------------------------------------------------ access key

    /// <summary>As typed or pasted; cleared once saved (never shown again).</summary>
    public string AccessKeyInput { get => _accessKeyInput; set => Set(ref _accessKeyInput, value); }

    // ------------------------------------------------------------------ cloud list and restore

    public ObservableCollection<CloudBackupRow> CloudBackups { get; } = [];

    public CloudBackupRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) IsCodeNeeded = false;
        }
    }

    /// <summary>The chosen backup needs the recovery code typed (another PC, or a backup made with an older code).</summary>
    public bool IsCodeNeeded { get => _isCodeNeeded; private set => Set(ref _isCodeNeeded, value); }

    public string RecoveryCodeInput { get => _recoveryCodeInput; set => Set(ref _recoveryCodeInput, value); }

    public bool IsRestorePrepared => _prepared is not null;

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

    public string PendingText => Pending is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, BackupText.RestorePending, Ltr(Pending.SourceFileName));

    // ------------------------------------------------------------------ loading

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => LoadAsync(cancellationToken));

    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var allowed = await _runner.QueryAsync(async (scope, ct) =>
        {
            var authorization = scope.Get<IAuthorizationService>();
            return (Manage: await authorization.IsAllowedAsync(CloudBackupCapabilities.Manage, ct),
                    Restore: await authorization.IsAllowedAsync(CloudBackupCapabilities.Restore, ct) && await authorization.IsAllowedAsync(BackupCapabilities.Restore, ct));
        }, cancellationToken);
        if (!Accept(allowed)) return;
        (CanManage, CanRestore) = allowed.Value;

        if (CanManage) await LoadOverviewAsync(cancellationToken);
        if (CanRestore) await LoadCloudAsync();
    }

    private async Task LoadOverviewAsync(CancellationToken cancellationToken = default)
    {
        var overview = await _runner.RunAsync((scope, ct) => scope.Get<GetCloudBackupOverviewQueryHandler>().HandleAsync(new GetCloudBackupOverviewQuery(), ct), cancellationToken);
        if (!Accept(overview)) return;
        var o = overview.Value;

        StateText = o.NotReadyBecause switch
        {
            null => CloudBackupText.StateReady,
            CloudSkipReason.NotLicensed => CloudBackupText.StateNotLicensed,
            CloudSkipReason.NoRecoveryCode => CloudBackupText.StateNoCode,
            CloudSkipReason.NoToken => CloudBackupText.StateNoAccess,
            _ => CloudBackupText.StateNoEscrow,
        };
        var active = o.Keys.FirstOrDefault(k => k.Active);
        HasCode = active is not null;
        CodeStateText = active is null ? CloudBackupText.CodeNone : string.Format(CultureInfo.CurrentCulture, CloudBackupText.CodeMade, Ltr(active.CreatedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
        AccessStateText = o.HasAccessKey ? CloudBackupText.AccessEntered : CloudBackupText.AccessNone;
        WaitingText = o.Waiting.Count switch
        {
            0 => CloudBackupText.WaitingNone,
            1 => CloudBackupText.WaitingOne,
            var n => string.Format(CultureInfo.CurrentCulture, CloudBackupText.Waiting, n),
        };
        LastUploadText = o.Status.LastUploadAt is { } at
            ? string.Format(CultureInfo.CurrentCulture, CloudBackupText.LastUpload, Ltr(at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), Ltr(o.Status.LastUploadedFile ?? string.Empty))
            : CloudBackupText.NoUploadYet;
        LastFailureText = o.Status.LastFailureAt is { } failedAt
            ? string.Format(CultureInfo.CurrentCulture, CloudBackupText.LastFailure, Ltr(failedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), Ltr(o.Status.LastFailure ?? string.Empty))
            : string.Empty;
    }

    private async Task LoadCloudAsync()
    {
        var listed = await _runner.RunAsync((scope, ct) => scope.Get<ListCloudBackupsQueryHandler>().HandleAsync(new ListCloudBackupsQuery(), ct));
        CloudBackups.Clear();
        Selected = null;
        if (!Accept(listed)) return;
        foreach (var item in listed.Value)
            CloudBackups.Add(new CloudBackupRow(item, Ltr(item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), Ltr(item.Label ?? item.Id.ToString("N")[..8]),
                Ltr(item.SizeBytes >= 1024 * 1024
                    ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", item.SizeBytes / (1024.0 * 1024.0))
                    : string.Format(CultureInfo.CurrentCulture, "{0} KB", Math.Max(1, item.SizeBytes / 1024)))));
    }

    // ------------------------------------------------------------------ recovery code

    private async Task MakeCodeAsync()
    {
        var setup = await _runner.RunAsync((scope, ct) => scope.Get<BeginRecoveryCodeCommandHandler>().HandleAsync(new BeginRecoveryCodeCommand(), ct));
        if (!Accept(setup)) return;
        ShowSetup(setup.Value);
        StatusMessage = null;
    }

    private async Task ConfirmCodeAsync()
    {
        var command = new ConfirmRecoveryCodeCommand(_setup!.SetupId, [FirstGroup, SecondGroup]);
        var confirmed = await _runner.RunAsync((scope, ct) => scope.Get<ConfirmRecoveryCodeCommandHandler>().HandleAsync(command, ct));
        if (!Accept(confirmed)) return;

        ShowSetup(null);
        StatusMessage = CloudBackupText.CodeConfirmed;
        await LoadOverviewAsync();
    }

    private void ShowSetup(RecoveryCodeSetup? setup)
    {
        _setup = setup;
        FirstGroup = string.Empty;
        SecondGroup = string.Empty;
        Raise(nameof(IsCodeShown));
        Raise(nameof(CodeText));
        Raise(nameof(FirstGroupLabel));
        Raise(nameof(SecondGroupLabel));
    }

    // ------------------------------------------------------------------ access key

    private async Task SaveAccessAsync()
    {
        var command = new SetBackupAccessCommand(AccessKeyInput);
        var saved = await _runner.RunAsync((scope, ct) => scope.Get<SetBackupAccessCommandHandler>().HandleAsync(command, ct));
        if (!Accept(saved)) return;

        AccessKeyInput = string.Empty;
        StatusMessage = CloudBackupText.AccessSaved;
        await LoadOverviewAsync();
        if (CanRestore) await LoadCloudAsync();
    }

    private async Task RemoveAccessAsync()
    {
        var removed = await _runner.RunAsync((scope, ct) => scope.Get<SetBackupAccessCommandHandler>().HandleAsync(new SetBackupAccessCommand(null), ct));
        if (!Accept(removed)) return;
        StatusMessage = CloudBackupText.AccessRemoved;
        await LoadOverviewAsync();
    }

    // ------------------------------------------------------------------ restore

    private async Task RestoreAsync()
    {
        var id = Selected!.Item.Id;
        var label = Selected.Item.Label;
        var code = IsCodeNeeded ? RecoveryCodeInput : null;
        var downloaded = await _runner.RunAsync((scope, ct) => scope.Get<DownloadForRestoreCommandHandler>().HandleAsync(new DownloadForRestoreCommand(id, code, label), ct));
        if (downloaded.IsFailure && downloaded.Error.Code is CloudBackupErrorCodes.NeedsRecoveryCode or CloudBackupErrorCodes.WrongRecoveryCode)
            IsCodeNeeded = true;
        if (!Accept(downloaded)) return;

        try
        {
            var path = downloaded.Value.PlainPath;
            var prepared = await _runner.RunAsync((scope, ct) => scope.Get<PrepareRestoreFromFileCommandHandler>().HandleAsync(new PrepareRestoreFromFileCommand(path), ct));
            if (!Accept(prepared)) return;

            _prepared = prepared.Value;
            RestoreQuestion = string.Format(CultureInfo.CurrentCulture, BackupText.RestoreQuestion,
                Ltr(downloaded.Value.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), Ltr(Selected?.Item.Label ?? prepared.Value.FileName));
            Raise(nameof(IsRestorePrepared));
            IsCodeNeeded = false;
            RecoveryCodeInput = string.Empty;
            StatusMessage = null;
        }
        finally
        {
            // the local restore has its own copy now (or nothing happened): the plain download goes
            var path = downloaded.Value.PlainPath;
            await _runner.RunAsync((scope, ct) => scope.Get<DiscardDownloadCommandHandler>().HandleAsync(new DiscardDownloadCommand(path), ct));
        }
    }

    private async Task ConfirmRestoreAsync()
    {
        var id = _prepared!.Id;
        var confirmed = await _runner.RunAsync((scope, ct) => scope.Get<ConfirmRestoreCommandHandler>().HandleAsync(new ConfirmRestoreCommand(id), ct));
        ClearPrepared();
        if (!Accept(confirmed)) return;
        Pending = confirmed.Value;
    }

    private async Task CancelRestoreAsync()
    {
        var cancelled = await _runner.RunAsync((scope, ct) => scope.Get<CancelRestoreCommandHandler>().HandleAsync(new CancelRestoreCommand(), ct));
        ClearPrepared();
        if (!Accept(cancelled)) return;
        Pending = null;
        StatusMessage = BackupText.RestoreCancelled;
    }

    private void ClearPrepared()
    {
        _prepared = null;
        RestoreQuestion = string.Empty;
        Raise(nameof(IsRestorePrepared));
    }

    /// <summary>Dates, sizes, names and the English business sentences keep their order inside Arabic text (FIX-13c).</summary>
    private static string Ltr(string value) => "‎" + value + "‎";
}
