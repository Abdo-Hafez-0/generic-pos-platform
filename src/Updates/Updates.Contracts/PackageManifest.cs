using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Updates.Contracts;

/// <summary>What an update package contains.</summary>
public enum PackageType
{
    /// <summary>The application core: Platform.*, Client.Host, Client.ModuleHost, Client.Licensing, Client.Updater, Client.Desktop.</summary>
    Core = 1,

    /// <summary>A single business module (module-owned code and migration metadata).</summary>
    Module = 2
}

/// <summary>A module a package requires, with a version constraint (same operators as the runtime VersionRange).</summary>
/// <param name="ModuleId">Required module (lowercase ID, e.g. "catalog").</param>
/// <param name="Operator">One of: ">=", "<=", "==", ">", "<".</param>
/// <param name="Version">Bound version, "Major.Minor.Patch".</param>
public sealed record PackageDependency(string ModuleId, string Operator, string Version);

/// <summary>One payload file: its path inside "payload/", SHA-256 (lowercase hex) and exact length.</summary>
public sealed record PackageFile(string Path, string Sha256, long Length);

/// <summary>
/// Database migration metadata for a MODULE package. The package carries NO SQL and no scripts: the migrations are
/// compiled into the module's own assemblies (module-owned, EF Core) and executed by the module itself.
/// </summary>
/// <param name="FromSchemaVersion">Schema version (IModuleManifest.DatabaseSchemaVersion) this update migrates from.</param>
/// <param name="ToSchemaVersion">Schema version after migration. Must be &gt;= FromSchemaVersion (schemas never move backwards).</param>
/// <param name="OldBinaryCompatibleWithNewSchema">
/// True when the PREVIOUS module binaries still work against the migrated schema (forward-compatible migration), so a
/// binary rollback after migration is safe. False means rolling back binaries alone is NOT safe.
/// </param>
public sealed record PackageMigration(int FromSchemaVersion, int ToSchemaVersion, bool OldBinaryCompatibleWithNewSchema);

/// <summary>
/// The SIGNED metadata of a package. Package-level security data (PayloadHash, KeyId) lives here, NOT in the runtime
/// IModuleManifest. Serialized to UTF-8 JSON; the signature covers exactly those bytes.
/// </summary>
/// <param name="SchemaVersion">Version of this manifest format (currently 1).</param>
/// <param name="PackageId">Unique ID of this package build.</param>
/// <param name="PackageType">Core or Module.</param>
/// <param name="TargetId">"core" for core packages; the ModuleId for module packages.</param>
/// <param name="Version">Version of the target delivered by this package ("Major.Minor.Patch"). Core and each module version independently.</param>
/// <param name="MinimumHostVersion">Module package: minimum installed core version it runs on. Core package: minimum installed core version it can upgrade from.</param>
/// <param name="MaximumHostVersion">Optional inclusive upper bound for the host version.</param>
/// <param name="TargetFramework">Runtime target the payload was built for (e.g. "net10.0").</param>
/// <param name="Dependencies">Modules (and minimum/exact versions) this package requires to be installed.</param>
/// <param name="RequiredModuleEntitlements">Module IDs the license must entitle (empty = no license requirement).</param>
/// <param name="RequiredFeatureEntitlements">Feature IDs the license must entitle.</param>
/// <param name="Migration">Module-owned migration metadata, or null when the update changes no schema.</param>
/// <param name="Publisher">Who published the package.</param>
/// <param name="CreatedAt">When the package was built.</param>
/// <param name="Files">Every payload file with its hash and length. Nothing outside this list is installable.</param>
/// <param name="PayloadHash">Digest over the Files list (see PayloadDigest): sorted "path TAB sha256 TAB length LF" lines, SHA-256, lowercase hex.</param>
/// <param name="KeyId">Identifier of the key that signs this manifest (also on the envelope).</param>
public sealed record PackageManifest(
    int SchemaVersion,
    Guid PackageId,
    PackageType PackageType,
    string TargetId,
    string Version,
    string MinimumHostVersion,
    string? MaximumHostVersion,
    string TargetFramework,
    IReadOnlyList<PackageDependency> Dependencies,
    IReadOnlyList<string> RequiredModuleEntitlements,
    IReadOnlyList<string> RequiredFeatureEntitlements,
    PackageMigration? Migration,
    string Publisher,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PackageFile> Files,
    string PayloadHash,
    string KeyId)
{
    public const int CurrentSchemaVersion = 1;
    public const string CoreTargetId = "core";
}

/// <summary>
/// The signed manifest envelope stored as manifest.json inside a package and returned by update discovery.
/// <see cref="Manifest"/> is Base64(UTF-8 JSON of <see cref="PackageManifest"/>) and is signed EXACTLY as transmitted.
/// Nothing in the envelope except KeyId/Algorithm (needed to look up the key) may be trusted before the signature verifies.
/// </summary>
public sealed record SignedPackageManifest(string Manifest, string KeyId, string Algorithm, string Signature);

public static class PackageManifestSerializer
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>The exact bytes that are signed.</summary>
    public static byte[] SerializeManifestBytes(PackageManifest manifest)
        => JsonSerializer.SerializeToUtf8Bytes(manifest, Options);

    public static string ToManifestText(byte[] manifestBytes) => Convert.ToBase64String(manifestBytes);

    /// <summary>Parses manifest bytes. Returns null when malformed. Does NOT verify anything.</summary>
    public static PackageManifest? TryParseManifest(byte[] manifestBytes)
    {
        try
        {
            return JsonSerializer.Deserialize<PackageManifest>(Encoding.UTF8.GetString(manifestBytes), Options);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string SerializeEnvelope(SignedPackageManifest envelope) => JsonSerializer.Serialize(envelope, Options);

    public static SignedPackageManifest? TryParseEnvelope(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SignedPackageManifest>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
