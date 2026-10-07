using Client.Desktop.Screens;
using Client.Desktop.Screens.Licensing;
using Client.Desktop.Shell;
using Client.Host.Hosting;
using Client.Licensing.Application;
using Integration.Tests;
using Licensing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Licensing;
using Platform.Core.Licensing;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;
using POS.UI.Screens;
using Tests.Common.Security;
using UI.Tests.Pos;

namespace UI.Tests.Licensing;

/// <summary>
/// The license screen and the shell on the production-like offline desktop, starting UNLICENSED (a fresh installation): activating through
/// the screen verifies the server's signed answer for real, and the shell unlocks the POS screen without a restart.
/// </summary>
[Collection(nameof(RealDesktop))]
public sealed class LicenseScreenOnRealDesktopTests
{
    /// <summary>A license server that answers every activation with a license this installation's trusted key signed.</summary>
    private sealed class SigningLicenseServer(SignedLicenseWorld world) : ILicenseClient
    {
        public List<ActivationRequest> Activations { get; } = [];

        public Task<ActivationResponse> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
        {
            Activations.Add(request);
            if (request.ActivationKey != "GPOS-VALID")
                return Task.FromResult(ActivationResponse.Failure("Licensing.Activation.KeyNotFound", "That activation key is not valid."));

            var now = OfflineDesktop.Start;
            return Task.FromResult(ActivationResponse.Success(world.Sign(now, now.AddDays(365), LicenseStatusClaim.Active, now.AddDays(30), now.AddDays(37), OfflineDesktop.AllModules)));
        }

        public Task<RenewalResponse> RenewAsync(RenewalRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ServerModule(ILicenseClient server) : IHostingModule
    {
        public void RegisterServices(HostBuilderContext context, IServiceCollection services) => services.AddSingleton(server);
    }

    [Fact]
    public async Task Activating_a_fresh_installation_through_the_screen_unlocks_the_POS_screen_in_the_shell()
    {
        var clock = new TestClock(OfflineDesktop.Start);
        using var licenses = new SignedLicenseWorld(OfflineDesktop.Start, clock);   // trusted key and installation, NO license issued
        var server = new SigningLicenseServer(licenses);
        await using var desktop = await OfflineDesktop.StartAsync(reuseFolder: null, keepFiles: false, licenses, clock, [new ServerModule(server)]);
        var services = desktop.Services;
        Assert.Equal(LicenseState.Unlicensed, services.GetRequiredService<ILicenseEntitlementService>().State);

        var runner = new UiActionRunner(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        var navigation = new NavigationBuilder([new DesktopScreens(), new PosScreens()], services.GetRequiredService<ICapabilityCatalog>(),
            services.GetRequiredService<ILicenseEntitlementService>());
        var shell = new ShellViewModel(runner, navigation, services.GetRequiredService<ICurrentUser>(), new FakeScreenFactory(), services.GetRequiredService<ILicenseService>());
        await shell.RefreshAsync();

        NavigationEntry Entry(string id) => shell.Groups.SelectMany(g => g.Entries).Single(e => e.Screen.Id == id);
        Assert.Equal(ScreenAvailability.LicenseLocked, Entry("pos.sell").Availability);
        Assert.True(Entry("licensing.license").IsAvailable);                       // license administration works in every license state

        var screen = new LicenseViewModel(runner, services.GetRequiredService<ILicenseService>(), shell);
        await screen.OnNavigatedToAsync();

        // a wrong key: the server's plain answer, nothing changes
        screen.ActivationKey = "GPOS-WRONG";
        screen.ActivateCommand.Execute(null);
        await WaitAsync(screen);
        Assert.Equal("That activation key is not valid.", screen.ErrorMessage);
        Assert.Equal(ScreenAvailability.LicenseLocked, Entry("pos.sell").Availability);

        // the right key: verified, stored, and the shell unlocks POS at once
        screen.ActivationKey = "GPOS-VALID";
        screen.ActivateCommand.Execute(null);
        await WaitAsync(screen);
        Assert.Null(screen.ErrorMessage);
        Assert.Equal(LicenseState.Active, services.GetRequiredService<ILicenseEntitlementService>().State);
        Assert.True(Entry("pos.sell").IsAvailable);
        Assert.Contains("pos", screen.ModulesText);
        Assert.Equal(screen.InstallationIdText, server.Activations[^1].InstallationId.ToString("D"));
        Assert.Contains("license: Active", shell.StatusText);
        Assert.True(File.Exists(Path.Combine(licenses.Directory, "license.json")));
    }

    private static async Task WaitAsync(LicenseViewModel vm)
    {
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }
}
