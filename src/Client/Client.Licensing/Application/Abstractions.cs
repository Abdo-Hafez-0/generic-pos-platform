using Client.Licensing.Domain;
using Licensing.Contracts;

namespace Client.Licensing.Application;

/// <summary>
/// Transport to the license server. The application layer depends on THIS abstraction, never on HttpClient.
/// Implementations (e.g. Client.Licensing.Http) must not throw for ordinary network failures: they return a
/// failure response with <see cref="LicenseErrorCodes.ServerUnreachable"/>.
/// A "success" response is never trusted by itself - the signed license inside is always verified.
/// </summary>
public interface ILicenseClient
{
    Task<ActivationResponse> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default);

    Task<RenewalResponse> RenewAsync(RenewalRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Persists the single current signed license locally (separate from business data).</summary>
public interface ILicenseStore
{
    /// <summary>Returns null when nothing is stored. A corrupt/unparseable file is reported as a license that will fail verification.</summary>
    Task<StoredLicense?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SignedLicense license, CancellationToken cancellationToken = default);
}

/// <summary>A stored license (possibly unparseable raw text).</summary>
/// <param name="License">The parsed license, or null if the stored data could not be parsed.</param>
public sealed record StoredLicense(SignedLicense? License);

/// <summary>Persists the installation identity (generated once, then reused).</summary>
public interface IInstallationIdentityStore
{
    Task<InstallationIdentity?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(InstallationIdentity identity, CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies a signed license against locally trusted PUBLIC keys. Pure and offline.
/// Only a license that passes is ever parsed into a trusted payload.
/// </summary>
public interface ILicenseVerifier
{
    LicenseVerificationResult Verify(SignedLicense license);
}

/// <summary>Static configuration the licensing service needs.</summary>
/// <param name="ProductId">The product this installation expects to be licensed for.</param>
public sealed record LicensingOptions(string ProductId);
