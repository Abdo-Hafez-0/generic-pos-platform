namespace Cloud.Contracts.Admin;

// ---- Customers -------------------------------------------------------------------------------------------------

public sealed record CustomerDto(
    Guid Id, string Name, string? ContactName, string? Email, string? Phone, string? Notes,
    bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CustomerRequest(string Name, string? ContactName, string? Email, string? Phone, string? Notes);

// ---- Licenses --------------------------------------------------------------------------------------------------

public sealed record LicenseDto(
    Guid LicenseId, string CustomerId, string ProductId, string Status,
    DateTimeOffset ValidFrom, DateTimeOffset ValidUntil,
    IReadOnlyList<string> Modules, IReadOnlyList<string> Features,
    Guid? InstallationId, DateTimeOffset? ActivatedAt, DateTimeOffset? LastIssuedAt,
    int Version, bool HasBackupAccess);

public sealed record CreateLicenseRequest(
    string CustomerId, string ProductId, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil,
    IReadOnlyList<string>? Modules, IReadOnlyList<string>? Features);

/// <summary>The activation key is returned ONCE, at creation. The server keeps only its hash.</summary>
public sealed record CreateLicenseResponse(LicenseDto License, string ActivationKey);

public sealed record ExtendLicenseRequest(DateTimeOffset ValidUntil);

public sealed record SetEntitlementsRequest(IReadOnlyList<string>? Modules, IReadOnlyList<string>? Features);

public sealed record StatusChangeRequest(string? Reason);

/// <summary>The backup access token is returned ONCE, when issued. The server keeps only its hash.</summary>
public sealed record BackupTokenResponse(Guid LicenseId, string Token);

public sealed record InstallationDto(
    Guid InstallationId, Guid LicenseId, string CustomerId, string ProductId, string LicenseStatus,
    DateTimeOffset? ActivatedAt, DateTimeOffset? LastIssuedAt);

// ---- Module registry & packages --------------------------------------------------------------------------------

public sealed record ModuleDto(
    string ModuleId, string DisplayName, string Description, string Category, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? LatestVersion, int PublishedVersions);

public sealed record RegisterModuleRequest(string ModuleId, string DisplayName, string? Description, string? Category);

public sealed record UpdateModuleRequest(string DisplayName, string? Description);

public sealed record ModuleDetailDto(ModuleDto Module, IReadOnlyList<PackageDto> Packages);

public sealed record PackageDto(
    Guid PackageId, string PackageType, string TargetId, string Version, string TargetFramework,
    string MinimumHostVersion, long SizeBytes, string Sha256, string KeyId, string Publisher, string Status,
    string? ReleaseNotes, DateTimeOffset PublishedAt, string PublishedBy, DateTimeOffset? WithdrawnAt);

// ---- Administration --------------------------------------------------------------------------------------------

public sealed record AuditEntryDto(Guid Id, DateTimeOffset At, string Actor, string Action, string EntityType, string EntityId, string Summary);

public sealed record DashboardDto(
    int Customers, int ActiveCustomers,
    int Licenses, int ActiveLicenses, int SuspendedLicenses, int RevokedLicenses, int Installations,
    int Modules, int PublishedPackages, int WithdrawnPackages,
    int Backups, long BackupBytes);
