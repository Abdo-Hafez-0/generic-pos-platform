using Client.Desktop.Shell;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Licensing;
using Platform.Core.Licensing;
using Platform.Core.Modules;
using Platform.Presentation.Screens;

namespace UI.Tests;

internal sealed class FakeView
{
}

internal sealed class FakeViewModel : INavigationAware
{
    public int Navigations { get; private set; }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        Navigations++;
        return Task.CompletedTask;
    }
}

internal sealed class ScreenProvider(params ScreenDescriptor[] screens) : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() => screens;
}

internal sealed class CapabilityProvider(params CapabilityDescriptor[] capabilities) : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => capabilities;
}

internal sealed class FakeLicensing(params string[] licensedModules) : ILicenseEntitlementService
{
    public LicenseState State { get; set; } = LicenseState.Active;

    public bool IsModuleLicensed(ModuleId moduleId) => licensedModules.Contains(moduleId.Value, StringComparer.OrdinalIgnoreCase);

    public bool IsFeatureLicensed(FeatureId featureId) => false;
}

internal sealed class FakeCurrentUser : ICurrentUser
{
    public bool IsAuthenticated { get; set; } = true;
    public Guid UserId { get; set; } = Guid.NewGuid();
    public string UserName { get; set; } = "cashier1";
    public string DisplayName { get; set; } = "First Cashier";
}

internal sealed class FakePermissions : IPermissionProvider
{
    public List<string> Held { get; } = [];
    public Exception? Failure { get; set; }
    public Guid? AskedFor { get; private set; }

    public Task<IReadOnlyCollection<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        AskedFor = userId;
        if (Failure is not null) throw Failure;
        return Task.FromResult<IReadOnlyCollection<string>>(Held.ToList());
    }
}

internal sealed class FakeScreenFactory : IScreenFactory
{
    public List<string> Created { get; } = [];

    public ScreenInstance Create(ScreenDescriptor screen)
    {
        Created.Add(screen.Id);
        return new ScreenInstance(new FakeView(), Activator.CreateInstance(screen.ViewModelType)!);
    }
}

internal static class Screens
{
    public static ScreenDescriptor Of(string id, string module, string group, string? capability = null, int order = 0, Type? view = null)
        => new(id, module, group, () => "Title of " + id, view ?? typeof(FakeView), typeof(FakeViewModel), capability, order);
}
