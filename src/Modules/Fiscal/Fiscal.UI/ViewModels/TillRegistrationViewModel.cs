using System.Runtime.InteropServices;
using Fiscal.Application.Commands;
using Fiscal.Application.DTOs;
using Fiscal.Domain.Enums;
using Fiscal.UI.Resources;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;

namespace Fiscal.UI.ViewModels;

/// <summary>An environment the window offers, with its words.</summary>
public sealed record EnvironmentChoice(FiscalEnvironment Environment, string Text);

/// <summary>
/// MISS-11c-3 (owner's choice, drawn option B): the window that registers this till with the tax authority - the environment, the client
/// ID and secret, the POS serial (Matjar's, shown to copy), the OS version, the model framework and the pre-shared key. The secrets come
/// from password boxes when Save is pressed; left empty, the kept ones stay. They are never shown again.
/// </summary>
public sealed class TillRegistrationViewModel : ViewModelBase
{
    private readonly IUiActionRunner _runner;
    private EnvironmentChoice? _environment;
    private string _clientId, _osVersion, _modelFramework;

    public TillRegistrationViewModel(IUiActionRunner runner, FiscalTillRegistrationDto? current, string? serial)
    {
        _runner = runner;
        Environments = [new(FiscalEnvironment.PreProduction, FiscalText.EnvironmentPreProduction), new(FiscalEnvironment.Production, FiscalText.EnvironmentProduction)];
        _environment = Environments.FirstOrDefault(e => e.Environment == current?.Environment);
        _clientId = current?.ClientId ?? string.Empty;
        _osVersion = current?.PosOsVersion ?? DefaultOsVersion();
        _modelFramework = current?.PosModelFramework ?? string.Empty;
        DeviceSerial = serial ?? string.Empty;
        HasSecrets = current?.HasSecrets == true;
        EnvironmentLocked = current?.EnvironmentLocked == true;
    }

    public IReadOnlyList<EnvironmentChoice> Environments { get; }
    public EnvironmentChoice? Environment { get => _environment; set => Set(ref _environment, value); }
    public string ClientId { get => _clientId; set => Set(ref _clientId, value); }
    public string PosOsVersion { get => _osVersion; set => Set(ref _osVersion, value); }
    public string PosModelFramework { get => _modelFramework; set => Set(ref _modelFramework, value); }

    /// <summary>Matjar's serial number for this till, to register on the portal as the POS serial.</summary>
    public string DeviceSerial { get; }

    /// <summary>Secrets are kept: the boxes may stay empty.</summary>
    public bool HasSecrets { get; }

    public string SecretHint => HasSecrets ? FiscalText.SecretKept : string.Empty;

    /// <summary>Documents reached the authority: the environment can no longer change (owner's decision).</summary>
    public bool EnvironmentLocked { get; }
    public bool CanChooseEnvironment => !EnvironmentLocked;

    /// <summary>Saves the registration with the secrets typed (empty = keep). True when saved.</summary>
    public async Task<bool> SaveAsync(string? clientSecret, string? presharedKey)
    {
        var saved = false;
        await BusyAsync(async () =>
        {
            var command = new SaveTillRegistrationCommand(Environment?.Environment ?? FiscalEnvironment.None, ClientId, clientSecret, PosOsVersion, PosModelFramework, presharedKey);
            var result = await _runner.RunAsync((scope, ct) => scope.Get<SaveTillRegistrationCommandHandler>().HandleAsync(command, ct));
            saved = Accept(result);
        });
        return saved;
    }

    /// <summary>"Windows 10.0.19045" - what the portal is told, unless the shop registered something else.</summary>
    private static string DefaultOsVersion()
    {
        var text = $"Windows {System.Environment.OSVersion.Version.Major}.{System.Environment.OSVersion.Version.Minor}.{System.Environment.OSVersion.Version.Build}";
        return OperatingSystem.IsWindows() ? text : RuntimeInformation.OSDescription.Length > 50 ? RuntimeInformation.OSDescription[..50] : RuntimeInformation.OSDescription;
    }
}
