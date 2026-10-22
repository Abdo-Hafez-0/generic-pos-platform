using System.Collections.ObjectModel;
using System.Globalization;
using Fiscal.Application.Abstractions;
using Fiscal.Application.Commands;
using Fiscal.Application.DTOs;
using Fiscal.Domain.Enums;
using Fiscal.UI.Resources;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;

namespace Fiscal.UI.ViewModels;

/// <summary>MISS-11d-3: one onboarding step as the window ticks it: waiting (○), working (◌), done (✓) or failed (✗) with ZATCA's words.</summary>
public sealed class OnboardingStepRow(FiscalOnboardingStep step, string text) : ViewModelBase
{
    private string _mark = "○", _detail = string.Empty;
    private bool _failed;

    public FiscalOnboardingStep Step { get; } = step;
    public string Text { get; } = text;
    public string Mark { get => _mark; private set => Set(ref _mark, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public bool Failed { get => _failed; private set => Set(ref _failed, value); }

    public void Show(FiscalOnboardingProgress progress)
    {
        (Mark, Failed) = progress switch
        {
            { Failure: not null } => ("✗", true),
            { Done: true } => ("✓", false),
            _ => ("◌", false),
        };
        Detail = progress.Failure ?? (progress.Step == FiscalOnboardingStep.EarlierInvoices && progress.Done ? progress.Detail ?? string.Empty : string.Empty);
    }

    public void Reset() => (Mark, Detail, Failed) = ("○", string.Empty, false);
}

/// <summary>
/// MISS-11d-3 (owner's choice A: one button, the steps ticked): the window that onboards this till with ZATCA - the environment (simulation
/// or production; owner's decision), the portal's OTP and the unit as the certificate names it; Onboard runs every step and ticks it. Once the
/// till is onboarded the same window renews its certificate with a new OTP (owner's decision).
/// </summary>
public sealed class OnboardingViewModel : ViewModelBase
{
    private readonly IUiActionRunner _runner;
    private EnvironmentChoice? _environment;
    private string _otp = string.Empty, _unitName, _branch, _industry, _location;
    private bool _finished;

    public OnboardingViewModel(IUiActionRunner runner, FiscalTillRegistrationDto? current, bool onboarded, string? suggestedLocation, string? tillName)
    {
        _runner = runner;
        IsRenewal = onboarded;
        Environments = [new(FiscalEnvironment.PreProduction, FiscalText.EnvironmentSimulation), new(FiscalEnvironment.Production, FiscalText.EnvironmentProduction)];
        _environment = Environments.FirstOrDefault(e => e.Environment == current?.Environment);
        _unitName = current?.UnitName ?? tillName ?? string.Empty;
        _branch = current?.UnitBranch ?? string.Empty;
        _industry = current?.UnitIndustry ?? string.Empty;
        _location = current?.UnitLocation ?? suggestedLocation ?? string.Empty;
        EnvironmentLocked = current?.EnvironmentLocked == true;
        Steps =
        [
            new(FiscalOnboardingStep.Key, FiscalText.StepKey), new(FiscalOnboardingStep.Request, FiscalText.StepRequest),
            new(FiscalOnboardingStep.ComplianceCertificate, FiscalText.StepComplianceCertificate), new(FiscalOnboardingStep.ComplianceChecks, FiscalText.StepComplianceChecks),
            new(FiscalOnboardingStep.ProductionCertificate, FiscalText.StepProductionCertificate), new(FiscalOnboardingStep.EarlierInvoices, FiscalText.StepEarlierInvoices),
        ];
    }

    /// <summary>The till is onboarded: the window renews its certificate (only the OTP is asked).</summary>
    public bool IsRenewal { get; }
    public bool IsOnboarding => !IsRenewal;
    public string Title => IsRenewal ? FiscalText.RenewTitle : FiscalText.OnboardingTitle;
    public string Hint => IsRenewal ? FiscalText.RenewHint : FiscalText.OnboardingHint;
    public string ActionText => IsRenewal ? FiscalText.Renew : FiscalText.Onboard;

    public IReadOnlyList<EnvironmentChoice> Environments { get; }
    public EnvironmentChoice? Environment { get => _environment; set => Set(ref _environment, value); }
    public bool EnvironmentLocked { get; }
    public bool CanChooseEnvironment => !EnvironmentLocked && IsOnboarding;

    public string Otp { get => _otp; set => Set(ref _otp, value); }
    public string UnitName { get => _unitName; set => Set(ref _unitName, value); }
    public string Branch { get => _branch; set => Set(ref _branch, value); }
    public string Industry { get => _industry; set => Set(ref _industry, value); }
    public string Location { get => _location; set => Set(ref _location, value); }

    public ObservableCollection<OnboardingStepRow> Steps { get; }

    /// <summary>Done (onboarded or renewed): the button closes the window.</summary>
    public bool Finished { get => _finished; private set { if (Set(ref _finished, value)) Raise(nameof(CanRun)); } }
    public bool CanRun => !Finished;

    /// <summary>Runs onboarding (or the renewal), ticking each step. True when the till is onboarded (or renewed).</summary>
    public async Task<bool> RunAsync()
    {
        var ok = false;
        await BusyAsync(async () =>
        {
            foreach (var row in Steps) row.Reset();
            if (IsRenewal)
            {
                var renewed = await _runner.RunAsync((scope, ct) => scope.Get<RenewTillCertificateCommandHandler>().HandleAsync(new RenewTillCertificateCommand(Otp), ct));
                if (!Accept(renewed)) return;
                StatusMessage = string.Format(CultureInfo.CurrentCulture, FiscalText.RenewDone, Day(renewed.Value));
                ok = true;
            }
            else
            {
                var progress = new Progress<FiscalOnboardingProgress>(p => Steps.FirstOrDefault(s => s.Step == p.Step)?.Show(p));   // on this (the UI) thread
                var command = new OnboardTillCommand(Environment?.Environment ?? FiscalEnvironment.None, Otp, UnitName, Branch, Industry, Location);
                var onboarded = await _runner.RunAsync((scope, ct) => scope.Get<OnboardTillCommandHandler>().HandleAsync(command, progress, ct));
                await Task.Yield();   // the last ticks arrive before the result is shown
                if (!Accept(onboarded)) return;
                StatusMessage = string.Format(CultureInfo.CurrentCulture, FiscalText.OnboardingDone, Day(onboarded.Value.CertificateExpiresAtUtc), onboarded.Value.EarlierSigned);
                ok = true;
            }

            Otp = string.Empty;   // used once
            Finished = true;
        });
        return ok;
    }

    private static string Day(DateTime utc) => "‎" + DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("d", CultureInfo.CurrentCulture) + "‎";
}
