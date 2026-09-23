namespace Client.Licensing;

/// <summary>
/// Marks the Client.Licensing project boundary.
///
/// STAGE 2 STATUS: Project boundary only. NO licensing implementation.
///
/// This project will own:
/// - Local license storage and reading
/// - Signed license lease verification (offline-capable)
/// - Entitlement lookup (which features are active)
/// - Activation and renewal coordination
/// - License state management
///
/// Implementation belongs to Stage 6.
///
/// IMPORTANT: Business modules must NEVER directly reference Client.Licensing.
/// Entitlement checks in business logic must go through Platform.Application.Abstractions.Authorization.ICurrentUser.
/// Client.Licensing will implement ICurrentUser (or provide its data) so that
/// business modules remain unaware of the licensing mechanism.
/// </summary>
public static class ClientLicensingAssemblyMarker
{
    // Intentionally empty.
    // Architecture tests use this type to locate the Client.Licensing assembly.
}
