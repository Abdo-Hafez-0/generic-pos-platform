using Client.Desktop.Resources;
using Client.Desktop.Screens.Licensing;
using Client.Licensing.Application;
using Client.Licensing.Domain;
using Licensing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Licensing;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Resources;
using Platform.Presentation.Screens;

namespace UI.Tests.Licensing;

/// <summary>The license screen (FIX-01e, done before FIX-01c): state in plain words, activate, renew, and the shell unlocks afterwards.</summary>
public sealed class LicenseViewModelTests
{
    private sealed class FakeLicenses : ILicenseService
    {
        public LicenseEvaluation Current { get; set; } = LicenseEvaluation.Unlicensed(DateTimeOffset.UtcNow);
        public Guid InstallationId { get; } = Guid.NewGuid();
        public Result<LicenseEvaluation>? ActivationAnswer { get; set; }
        public Result<LicenseEvaluation>? RenewalAnswer { get; set; }
        public Exception? Throw { get; set; }
        public List<string> Keys { get; } = [];

        public Task<LicenseEvaluation> InitializeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<InstallationIdentity> GetInstallationIdentityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new InstallationIdentity(InstallationId, DateTimeOffset.UtcNow));

        public Task<Result<LicenseEvaluation>> ActivateAsync(string activationKey, CancellationToken cancellationToken = default)
        {
            if (Throw is { } failure) throw failure;
            Keys.Add(activationKey);
            if (ActivationAnswer is { IsSuccess: true } ok) Current = ok.Value;
            return Task.FromResult(ActivationAnswer!);
        }

        public Task<Result<LicenseEvaluation>> RenewAsync(CancellationToken cancellationToken = default)
        {
            if (RenewalAnswer is { IsSuccess: true } ok) Current = ok.Value;
            return Task.FromResult(RenewalAnswer!);
        }
    }

    private sealed class FakeShell : IShellNavigation
    {
        public int Refreshes { get; private set; }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            Refreshes++;
            return Task.CompletedTask;
        }
    }

    private sealed class Authorization : IAuthorizationService
    {
        public bool Allow { get; set; } = true;

        public Task<Result> AuthorizeAsync(string capability, CancellationToken cancellationToken = default)
            => Task.FromResult(Allow ? Result.Success() : Result.Failure(SecurityErrors.Forbidden(capability)));

        public Task<bool> IsAllowedAsync(string capability, CancellationToken cancellationToken = default) => Task.FromResult(Allow);
    }

    private readonly FakeLicenses _licenses = new();
    private readonly FakeShell _shell = new();
    private readonly Authorization _authorization = new();
    private readonly LicenseViewModel _vm;

    public LicenseViewModelTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILicenseService>(_licenses);
        services.AddSingleton<IAuthorizationService>(_authorization);
        services.AddTransient<ActivateLicenseCommandHandler>();
        services.AddTransient<RenewLicenseCommandHandler>();
        var runner = new UiActionRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        _vm = new LicenseViewModel(runner, _licenses, _shell);
    }

    private static LicenseEvaluation Active(DateTimeOffset validUntil, DateTimeOffset leaseUntil, params string[] modules)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new LicensePayload(Guid.NewGuid(), "Corner Shop Ltd", Guid.NewGuid(), "genericpos", 1, now, now.AddDays(-1), validUntil,
            leaseUntil, leaseUntil.AddDays(7), LicenseStatusClaim.Active, modules, [], "vendor", "key-1");
        return new LicenseEvaluation(LicenseState.Active, payload, InvalidReason.None, ExpiryKind.None, true, now);
    }

    private async Task Run(System.Windows.Input.ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 200 && _vm.IsBusy; i++) await Task.Delay(5);
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task An_unlicensed_installation_says_so_and_shows_the_installation_id()
    {
        await _vm.OnNavigatedToAsync();

        Assert.Equal(LicenseText.StateUnlicensed, _vm.StateText);
        Assert.True(_vm.IsRestricted);
        Assert.Contains("not activated", _vm.NoticeText);
        Assert.Equal(_licenses.InstallationId.ToString("D"), _vm.InstallationIdText);
        Assert.Equal(LicenseText.NotAvailable, _vm.CustomerText);
        Assert.False(_vm.HasVerifiedLicense);
        Assert.False(_vm.RenewCommand.CanExecute(null));
    }

    [Fact]
    public void Activation_needs_a_key()
    {
        Assert.False(_vm.ActivateCommand.CanExecute(null));
        _vm.ActivationKey = "   ";
        Assert.False(_vm.ActivateCommand.CanExecute(null));
        _vm.ActivationKey = "GPOS-1234";
        Assert.True(_vm.ActivateCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_successful_activation_shows_the_license_forgets_the_key_and_unlocks_the_shell()
    {
        var validUntil = DateTimeOffset.UtcNow.AddYears(1);
        _licenses.ActivationAnswer = Result.Success(Active(validUntil, DateTimeOffset.UtcNow.AddDays(30), "pos", "catalog"));
        _vm.ActivationKey = "GPOS-1234";

        await Run(_vm.ActivateCommand);

        Assert.Equal(["GPOS-1234"], _licenses.Keys);
        Assert.Equal(LicenseText.StateActive, _vm.StateText);
        Assert.False(_vm.IsRestricted);
        Assert.Equal("Corner Shop Ltd", _vm.CustomerText);
        Assert.Equal("catalog, pos", _vm.ModulesText);
        Assert.Equal(validUntil.ToLocalTime().ToString("d"), _vm.ValidUntilText);
        Assert.Equal(string.Empty, _vm.ActivationKey);
        Assert.Equal(LicenseText.Activated, _vm.StatusMessage);
        Assert.Equal(1, _shell.Refreshes);
        Assert.True(_vm.RenewCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_refused_activation_says_why_keeps_the_key_and_changes_nothing()
    {
        _licenses.ActivationAnswer = Result.Failure<LicenseEvaluation>(Error.Failure("Licensing.ServerUnreachable",
            "The license server could not be reached. Local operation is not affected; try again when the connection is back."));
        _vm.ActivationKey = "GPOS-1234";

        await Run(_vm.ActivateCommand);

        Assert.StartsWith("The license server could not be reached.", _vm.ErrorMessage);
        Assert.Equal("GPOS-1234", _vm.ActivationKey);
        Assert.Equal(LicenseText.StateUnlicensed, _vm.StateText);
        Assert.Equal(0, _shell.Refreshes);
    }

    [Fact]
    public async Task Someone_without_licensing_manage_is_refused_by_the_handler_not_just_the_menu()
    {
        _authorization.Allow = false;
        _vm.ActivationKey = "GPOS-1234";

        await Run(_vm.ActivateCommand);

        Assert.Contains("permission", _vm.ErrorMessage);
        Assert.Empty(_licenses.Keys);
    }

    [Fact]
    public async Task An_unexpected_failure_shows_the_plain_message()
    {
        _licenses.Throw = new IOException("Access to C:\\Users\\x\\license.json is denied");
        _vm.ActivationKey = "GPOS-1234";

        await Run(_vm.ActivateCommand);

        Assert.Equal(PresentationText.OperationFailed, _vm.ErrorMessage);
    }

    [Fact]
    public async Task Renewing_updates_the_renew_by_date_and_refreshes_the_shell()
    {
        _licenses.Current = Active(DateTimeOffset.UtcNow.AddYears(1), DateTimeOffset.UtcNow.AddDays(1), "pos");
        var renewedLease = DateTimeOffset.UtcNow.AddDays(30);
        _licenses.RenewalAnswer = Result.Success(Active(DateTimeOffset.UtcNow.AddYears(1), renewedLease, "pos"));
        await _vm.OnNavigatedToAsync();

        await Run(_vm.RenewCommand);

        Assert.Equal(renewedLease.ToLocalTime().ToString("d"), _vm.RenewByText);
        Assert.Equal(LicenseText.Renewed, _vm.StatusMessage);
        Assert.Equal(1, _shell.Refreshes);
    }
}
