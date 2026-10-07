using System.Globalization;
using System.Windows.Input;
using Client.Desktop.Resources;
using Client.Licensing.Application;
using Client.Licensing.Domain;
using Platform.Core.Licensing;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Client.Desktop.Screens.Licensing;

/// <summary>
/// The license screen (FIX-01e, moved ahead of FIX-01c): what the installation is licensed for, in plain words, and the two things an
/// administrator can do about it - activate with an activation key, renew now.
///
/// Every decision stays in Client.Licensing: the handlers authorize (licensing.manage, available in EVERY license state so an expired
/// installation can always be fixed), the service talks to the license server and verifies the signed answer. After a change the shell
/// rebuilds its navigation, so licensed screens unlock at once. Activation needs the Internet once; selling never does.
/// </summary>
public sealed class LicenseViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private readonly ILicenseService _licenses;
    private readonly IShellNavigation _shell;

    private string _activationKey = string.Empty;
    private string _stateText = string.Empty;
    private string _noticeText = string.Empty;
    private bool _isRestricted;
    private string _customerText = string.Empty;
    private string _validUntilText = string.Empty;
    private string _renewByText = string.Empty;
    private string _modulesText = string.Empty;
    private string _installationIdText = string.Empty;
    private bool _hasVerifiedLicense;

    public LicenseViewModel(IUiActionRunner runner, ILicenseService licenses, IShellNavigation shell)
    {
        _runner = runner;
        _licenses = licenses;
        _shell = shell;
        ActivateCommand = Command(ActivateAsync, () => !string.IsNullOrWhiteSpace(ActivationKey));
        RenewCommand = Command(RenewAsync, () => HasVerifiedLicense);
        Show(licenses.Current);
    }

    public ICommand ActivateCommand { get; }
    public ICommand RenewCommand { get; }

    /// <summary>The key as typed. Never logged, never kept after a successful activation.</summary>
    public string ActivationKey { get => _activationKey; set => Set(ref _activationKey, value); }

    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }

    /// <summary>The plain explanation of the state (empty when everything is fine).</summary>
    public string NoticeText { get => _noticeText; private set => Set(ref _noticeText, value); }

    /// <summary>True when the state restricts licensed functionality (the notice is shown as a warning).</summary>
    public bool IsRestricted { get => _isRestricted; private set => Set(ref _isRestricted, value); }

    public string CustomerText { get => _customerText; private set => Set(ref _customerText, value); }
    public string ValidUntilText { get => _validUntilText; private set => Set(ref _validUntilText, value); }
    public string RenewByText { get => _renewByText; private set => Set(ref _renewByText, value); }
    public string ModulesText { get => _modulesText; private set => Set(ref _modulesText, value); }
    public string InstallationIdText { get => _installationIdText; private set => Set(ref _installationIdText, value); }

    /// <summary>True when a signed license was verified (renewal is possible).</summary>
    public bool HasVerifiedLicense { get => _hasVerifiedLicense; private set => Set(ref _hasVerifiedLicense, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(async () =>
    {
        Show(_licenses.Current);
        var identity = await _runner.QueryAsync((scope, ct) => scope.Get<ILicenseService>().GetInstallationIdentityAsync(ct), cancellationToken);
        if (Accept(identity))
            InstallationIdText = identity.Value.InstallationId.ToString("D");
    });

    private async Task ActivateAsync()
    {
        var key = ActivationKey;
        var activated = await _runner.RunAsync((scope, ct) => scope.Get<ActivateLicenseCommandHandler>().HandleAsync(new ActivateLicenseCommand(key), ct));
        if (!Accept(activated)) return;

        ActivationKey = string.Empty;
        Show(activated.Value);
        StatusMessage = LicenseText.Activated;
        await _shell.RefreshAsync();
    }

    private async Task RenewAsync()
    {
        var renewed = await _runner.RunAsync((scope, ct) => scope.Get<RenewLicenseCommandHandler>().HandleAsync(new RenewLicenseCommand(), ct));
        if (!Accept(renewed)) return;

        Show(renewed.Value);
        StatusMessage = LicenseText.Renewed;
        await _shell.RefreshAsync();
    }

    private void Show(LicenseEvaluation evaluation)
    {
        StateText = Describe(evaluation.State);
        var notice = LicenseNotice.Describe(evaluation);
        NoticeText = notice.Message;
        IsRestricted = notice.Level == LicenseNoticeLevel.Restricted;

        var payload = evaluation.Payload;
        HasVerifiedLicense = payload is not null;
        CustomerText = payload?.CustomerId ?? LicenseText.NotAvailable;
        ValidUntilText = payload is null ? LicenseText.NotAvailable : FormatDate(payload.ValidUntil);
        RenewByText = payload is null ? LicenseText.NotAvailable : FormatDate(payload.LeaseValidUntil);
        ModulesText = payload is null || payload.Modules.Count == 0
            ? LicenseText.NotAvailable
            : string.Join(", ", payload.Modules.OrderBy(m => m, StringComparer.OrdinalIgnoreCase));
    }

    private static string FormatDate(DateTimeOffset value) => value.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

    private static string Describe(LicenseState state) => state switch
    {
        LicenseState.Active => LicenseText.StateActive,
        LicenseState.GracePeriod => LicenseText.StateGracePeriod,
        LicenseState.Expired => LicenseText.StateExpired,
        LicenseState.Suspended => LicenseText.StateSuspended,
        LicenseState.Revoked => LicenseText.StateRevoked,
        LicenseState.Invalid => LicenseText.StateInvalid,
        _ => LicenseText.StateUnlicensed,
    };
}
