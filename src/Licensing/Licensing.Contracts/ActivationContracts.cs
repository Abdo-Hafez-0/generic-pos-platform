namespace Licensing.Contracts;

/// <summary>Well-known error codes returned by the license server.</summary>
public static class LicenseErrorCodes
{
    public const string InvalidRequest = "License.InvalidRequest";
    public const string NotFound = "License.NotFound";
    public const string AlreadyActivated = "License.AlreadyActivated";
    public const string ProductMismatch = "License.ProductMismatch";
    public const string InstallationMismatch = "License.InstallationMismatch";
    public const string Suspended = "License.Suspended";
    public const string Revoked = "License.Revoked";
    public const string ServerUnreachable = "Licensing.Server.Unreachable";
    public const string ServerRejected = "Licensing.Server.Rejected";
}

/// <summary>Client -> server: bind a license (identified by its activation key) to this installation.</summary>
public sealed record ActivationRequest(string ActivationKey, Guid InstallationId, string ProductId);

/// <summary>Server -> client. On success <see cref="License"/> is signed; the client MUST verify it before trusting it.</summary>
public sealed record ActivationResponse(bool IsSuccess, SignedLicense? License, string? ErrorCode, string? ErrorMessage)
{
    public static ActivationResponse Success(SignedLicense license) => new(true, license, null, null);
    public static ActivationResponse Failure(string code, string message) => new(false, null, code, message);
}

/// <summary>Client -> server: obtain a fresh signed license/lease for an already-activated license.</summary>
public sealed record RenewalRequest(Guid LicenseId, Guid InstallationId, int CurrentLicenseVersion);

/// <summary>Server -> client. On success <see cref="License"/> is signed (it may carry Suspended/Revoked status).</summary>
public sealed record RenewalResponse(bool IsSuccess, SignedLicense? License, string? ErrorCode, string? ErrorMessage)
{
    public static RenewalResponse Success(SignedLicense license) => new(true, license, null, null);
    public static RenewalResponse Failure(string code, string message) => new(false, null, code, message);
}
