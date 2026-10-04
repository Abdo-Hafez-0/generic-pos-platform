namespace Client.Licensing;

/// <summary>
/// Marks the Client.Licensing assembly (used by Architecture.Tests to locate it).
///
/// Client.Licensing owns, on the client side:
///   Domain          - installation identity, license evaluation (deterministic, pure), license policy
///   Application     - ILicenseService (activate / renew / evaluate), and the abstractions it needs:
///                     ILicenseClient (server transport), ILicenseStore, IInstallationIdentityStore, ILicenseVerifier
///   Infrastructure  - file-based stores, ECDSA signature verification, host registration
///
/// It contains NO HTTP (see Client.Licensing.Http), NO EF Core / database access, NO WPF and NO reference to any
/// business module. Business modules never reference it: they see only
/// Platform.Application.Abstractions.Licensing.ILicenseEntitlementService.
///
/// Licensing restricts ACCESS to licensed functionality; it never deletes or modifies business data.
/// </summary>
public static class ClientLicensingAssemblyMarker
{
    // Intentionally empty.
}
