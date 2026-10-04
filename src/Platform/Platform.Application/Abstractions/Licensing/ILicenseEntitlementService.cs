using Platform.Core.Licensing;
using Platform.Core.Modules;

namespace Platform.Application.Abstractions.Licensing;

/// <summary>
/// Platform-level, read-only view of what the installation is licensed for.
///
/// Business modules and the module runtime depend on THIS abstraction only - never on Client.Licensing,
/// the license server or licensing persistence. Answers come from the locally verified license; no network
/// call is ever made, so offline POS operation is unaffected.
///
/// Licensing restricts ACCESS to licensed functionality. It never deletes or alters business data.
/// </summary>
public interface ILicenseEntitlementService
{
    /// <summary>The current state, evaluated against the current time.</summary>
    LicenseState State { get; }

    /// <summary>True when the current state grants entitlements AND the module is in the signed license.</summary>
    bool IsModuleLicensed(ModuleId moduleId);

    /// <summary>True when the current state grants entitlements AND the feature is in the signed license.</summary>
    bool IsFeatureLicensed(FeatureId featureId);
}

public static class LicenseEntitlementExtensions
{
    /// <summary>Checks a module manifest's ModuleId against the license (manifest-to-entitlement link).</summary>
    public static bool IsLicensed(this ILicenseEntitlementService service, IModuleManifest manifest)
        => service.IsModuleLicensed(manifest.ModuleId);
}
