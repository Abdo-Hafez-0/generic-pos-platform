namespace Updates.Contracts;

/// <summary>Well-known update error codes (also used as Error codes by Client.Updater).</summary>
public static class UpdateErrorCodes
{
    public const string NotFound = "Update.NotFound";
    public const string ServerUnavailable = "Update.ServerUnavailable";
    public const string InvalidPackage = "Update.InvalidPackage";
    public const string SignatureInvalid = "Update.SignatureInvalid";
    public const string HashMismatch = "Update.HashMismatch";
    public const string UnknownSigningKey = "Update.UnknownSigningKey";
    public const string Incompatible = "Update.Incompatible";
    public const string AlreadyInstalled = "Update.AlreadyInstalled";
    public const string Downgrade = "Update.Downgrade";
    public const string DependencyMissing = "Update.DependencyMissing";
    public const string DependencyConflict = "Update.DependencyConflict";
    public const string LicenseRequired = "Update.LicenseRequired";
    public const string MigrationFailed = "Update.MigrationFailed";
    public const string InvalidMigration = "Update.InvalidMigration";
    public const string InstallationFailed = "Update.InstallationFailed";
    public const string RollbackFailed = "Update.RollbackFailed";
    public const string RecoveryRequired = "Update.RecoveryRequired";
    public const string DownloadFailed = "Update.DownloadFailed";
}

/// <summary>Something installed on the client: "core" or a module, with its version.</summary>
public sealed record InstalledTarget(string TargetId, string Version);

/// <summary>Client -> update source: "are there updates for me?" Contains only versions, never customer data.</summary>
public sealed record UpdateCheckRequest(string HostVersion, string TargetFramework, IReadOnlyList<InstalledTarget> Installed);

/// <summary>One available update. The signed manifest lets the client verify and filter BEFORE downloading.</summary>
/// <param name="DownloadSha256">Advisory SHA-256 of the whole package file (early corruption detection). Authenticity comes from the signed manifest, not from this value.</param>
public sealed record UpdateInfo(
    Guid PackageId,
    PackageType PackageType,
    string TargetId,
    string Version,
    long SizeBytes,
    string DownloadSha256,
    SignedPackageManifest SignedManifest);

public sealed record UpdateCheckResponse(bool IsSuccess, IReadOnlyList<UpdateInfo> Updates, string? ErrorCode, string? ErrorMessage)
{
    public static UpdateCheckResponse Success(IReadOnlyList<UpdateInfo> updates) => new(true, updates, null, null);
    public static UpdateCheckResponse Failure(string code, string message) => new(false, [], code, message);
}
