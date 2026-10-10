using Platform.Application.Abstractions.Licensing;
using Platform.Core.Modules;

namespace Platform.Application.Abstractions.Authorization;

/// <summary>
/// The license half of authorization, for work the application does on its own with nobody signed in (MISS-04e2: uploading the encrypted
/// copy of a scheduled backup). It answers "may the owner of this capability work at all under the current license?" from the same
/// capability catalog and the same locally verified license as <see cref="AuthorizationService"/>, so license enforcement stays in one
/// place and modules still never see licensing (ARCH-SEC-005). It says nothing about people: every user action still goes through
/// <see cref="IAuthorizationService"/>.
/// </summary>
public interface IUnattendedWorkGate
{
    /// <summary>False for an unknown capability; true for one that needs no license; otherwise whether its module is licensed now.</summary>
    bool IsLicensed(string capability);
}

public sealed class UnattendedWorkGate(ICapabilityCatalog catalog, ILicenseEntitlementService? licensing = null) : IUnattendedWorkGate
{
    public bool IsLicensed(string capability)
    {
        if (catalog.Find(capability) is not { } descriptor) return false;
        if (descriptor.License == LicenseRequirement.None || licensing is null) return true;
        return licensing.IsModuleLicensed(new ModuleId(descriptor.Module));
    }
}
