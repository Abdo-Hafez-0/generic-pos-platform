using System.Security.Cryptography;
using System.Text;
using Client.Updater.Domain;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Licensing;
using Platform.Application.Abstractions.Security;
using Platform.Application.Modules;
using Platform.Core.Modules;
using Platform.Core.Results;
using Security.Es256;
using Updates.Contracts;
using Updates.Package;

namespace Client.Updater.Application;

/// <summary>
/// The deterministic verification pipeline. It NEVER mutates the installation (no staging, no writes).
///
/// Order (first failure stops; fail closed):
///   1 package exists / 2 opens safely (limits, safe paths, only manifest.json + payload/)
///   3 envelope well-formed
///   4 signing key is TRUSTED (unknown key rejected)     5 signature valid over the exact manifest bytes
///   --- only now is the manifest parsed and trusted ---
///   6 manifest well-formed: schema, package type, identity (target/module id, versions, file list)
///   7 payload: exactly the listed files, safe paths, no scripts, per-file SHA-256 + length, PayloadHash
///   8 target framework + host compatibility   9 version semantics (upgrade only; same/downgrade rejected)
///  10 module compatibility with the host (module and installed modules)
///  11 dependencies (reuses ModuleDependencyResolver: missing, version conflict, cycles)
///  12 license entitlements (asks ILicenseEntitlementService; never re-implements licensing)
///  13 migration metadata (module packages only; schema never moves backwards)
///  14 installation plan
/// Steps 3-6 can also run on a discovered manifest alone (<see cref="VerifyManifest"/>), skipping the payload step.
/// </summary>
public sealed class PackageVerifier(
    Es256Verifier signatureVerifier,
    IInstalledStateProvider installedState,
    ILicenseEntitlementService licensing,
    UpdaterOptions options,
    ILogger<PackageVerifier> logger,
    ISecurityEventSink? events = null)
{
    private static readonly ModuleDependencyResolver Resolver = new();

    /// <summary>Verifies a package file end to end (steps 1-14). Nothing is extracted or modified.</summary>
    public Result<VerifiedPackage> VerifyPackage(string packagePath)
    {
        if (!File.Exists(packagePath))
            return Reject(UpdateErrorCodes.NotFound, "The package file does not exist.");

        var opened = PackageReader.Open(packagePath);
        if (!opened.IsSuccess)
            return Reject(UpdateErrorCodes.InvalidPackage, opened.ErrorMessage ?? "The package cannot be opened.");

        using var contents = opened.Contents!;
        return Run(contents.Envelope, contents, packagePath);
    }

    /// <summary>Verifies discovery metadata only (signature, compatibility, version, dependencies, license, migration).</summary>
    public Result<VerifiedPackage> VerifyManifest(SignedPackageManifest envelope) => Run(envelope, null, null);

    private Result<VerifiedPackage> Run(SignedPackageManifest envelope, PackageContents? contents, string? path)
    {
        // 3 + 4 + 5: signature first; nothing in the manifest is trusted before this passes.
        if (string.IsNullOrEmpty(envelope.Manifest) || string.IsNullOrEmpty(envelope.Signature) || string.IsNullOrEmpty(envelope.KeyId))
            return Reject(UpdateErrorCodes.InvalidPackage, "The manifest envelope is malformed.");

        byte[] manifestBytes;
        byte[] signature;
        try
        {
            manifestBytes = Convert.FromBase64String(envelope.Manifest);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            return Reject(UpdateErrorCodes.InvalidPackage, "The manifest envelope is not valid Base64.");
        }

        var check = signatureVerifier.Verify(envelope.KeyId, envelope.Algorithm, manifestBytes, signature);
        switch (check)
        {
            case SignatureCheck.UnknownKey:
                return Reject(UpdateErrorCodes.UnknownSigningKey, $"The signing key '{envelope.KeyId}' is not trusted.");
            case SignatureCheck.InvalidSignature:
                return Reject(UpdateErrorCodes.SignatureInvalid, "The package signature is invalid.");
            case SignatureCheck.UnsupportedAlgorithm:
                return Reject(UpdateErrorCodes.SignatureInvalid, "The signature algorithm is not supported.");
            case SignatureCheck.Valid:
                break;
            default:
                return Reject(UpdateErrorCodes.InvalidPackage, "The signature is malformed.");
        }

        // 6: parse + validate identity (now trusted bytes).
        var manifest = PackageManifestSerializer.TryParseManifest(manifestBytes);
        if (manifest is null)
            return Reject(UpdateErrorCodes.InvalidPackage, "The manifest is malformed.");

        if (!string.Equals(manifest.KeyId, envelope.KeyId, StringComparison.Ordinal))
            return Reject(UpdateErrorCodes.InvalidPackage, "The manifest key ID does not match the envelope.");

        var identity = ValidateIdentity(manifest, out var candidate);
        if (identity is not null) return identity;

        // 7: payload integrity (only when we have the package itself).
        if (contents is not null)
        {
            var payload = VerifyPayload(manifest, contents);
            if (payload is not null) return payload;
        }
        else if (!string.Equals(PayloadDigest.Compute(manifest.Files), manifest.PayloadHash, StringComparison.Ordinal))
        {
            return Reject(UpdateErrorCodes.HashMismatch, "The manifest payload hash does not match its file list.");
        }

        // 8: runtime + host compatibility.
        if (!string.Equals(manifest.TargetFramework, options.TargetFramework, StringComparison.OrdinalIgnoreCase))
            return Reject(UpdateErrorCodes.Incompatible,
                $"The package targets '{manifest.TargetFramework}' but this installation runs '{options.TargetFramework}'.");

        var host = installedState.HostVersion;
        var minHost = ModuleVersion.Parse(manifest.MinimumHostVersion);
        if (host < minHost)
            return Reject(UpdateErrorCodes.Incompatible, $"The package requires host version {minHost} or newer (installed: {host}).");

        if (manifest.MaximumHostVersion is { } maxText && host > ModuleVersion.Parse(maxText))
            return Reject(UpdateErrorCodes.Incompatible, $"The package supports host versions up to {maxText} (installed: {host}).");

        // 9: version semantics.
        var installedModules = installedState.GetInstalledModules();
        ModuleVersion? installedVersion = manifest.PackageType == PackageType.Core
            ? host
            : installedModules.FirstOrDefault(m => string.Equals(m.Id.Value, manifest.TargetId, StringComparison.Ordinal))?.Version;

        var relation = VersionSemantics.Compare(installedVersion, candidate);
        if (relation == VersionRelation.Same)
            return Reject(UpdateErrorCodes.AlreadyInstalled, $"Version {candidate} of '{manifest.TargetId}' is already installed.", conflict: true);

        if (relation == VersionRelation.Downgrade)
            return Reject(UpdateErrorCodes.Downgrade,
                $"Version {candidate} is older than the installed {installedVersion}. Downgrades are not accepted (use rollback).", conflict: true);

        // 10 + 11: compatibility with installed modules and dependencies.
        var depResult = manifest.PackageType == PackageType.Core
            ? CheckCoreUpdate(manifest, candidate, installedModules)
            : CheckModuleUpdate(manifest, candidate, installedModules, host);
        if (depResult is not null) return depResult;

        // 12: licensing.
        var license = CheckLicense(manifest);
        if (license is not null) return license;

        // 13: migration metadata.
        var migration = CheckMigration(manifest, installedModules);
        if (migration is not null) return migration;

        // 14: plan.
        logger.LogInformation("Package {PackageId} ({Target} {Version}) verified: {Relation}",
            manifest.PackageId, manifest.TargetId, manifest.Version, relation);

        return Result.Success(new VerifiedPackage(manifest, envelope, relation, installedVersion, candidate, path));
    }

    // ---------------------------------------------------------------- identity

    private Result<VerifiedPackage>? ValidateIdentity(PackageManifest m, out ModuleVersion candidate)
    {
        candidate = new ModuleVersion(0, 0, 0);

        // The same structural rules the packaging tools enforce before signing.
        var violation = ManifestRules.ValidateStructure(m);
        if (violation is not null)
            return Reject(violation.Code, violation.Message, conflict: violation.Code == UpdateErrorCodes.DependencyConflict);

        candidate = ModuleVersion.Parse(m.Version);
        return null;
    }

    private static bool TryParseDependency(PackageDependency d, out ModuleDependency? dependency)
    {
        dependency = null;
        if (string.IsNullOrWhiteSpace(d.ModuleId) || !ModuleVersion.TryParse(d.Version, out var v) || v is null) return false;

        VersionRangeOperator? op = d.Operator switch
        {
            ">=" => VersionRangeOperator.GreaterThanOrEqual,
            "<=" => VersionRangeOperator.LessThanOrEqual,
            "==" => VersionRangeOperator.ExactMatch,
            ">" => VersionRangeOperator.GreaterThan,
            "<" => VersionRangeOperator.LessThan,
            _ => null
        };

        if (op is null) return false;
        dependency = new ModuleDependency(new ModuleId(d.ModuleId), new VersionRange(v, op.Value));
        return true;
    }

    // ---------------------------------------------------------------- payload

    private Result<VerifiedPackage>? VerifyPayload(PackageManifest m, PackageContents contents)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in m.Files)
        {
            if (!PackageFormat.IsSafeRelativePath(file.Path))
                return Reject(UpdateErrorCodes.InvalidPackage, $"The manifest lists an unsafe path '{file.Path}'.");

            if (PackageFormat.IsForbiddenFile(file.Path, m.PackageType))
                return Reject(UpdateErrorCodes.InvalidPackage, $"File '{file.Path}' is not allowed in a {m.PackageType} package (scripts/installers/executables are forbidden).");

            if (!listed.Add(file.Path))
                return Reject(UpdateErrorCodes.InvalidPackage, $"The manifest lists '{file.Path}' twice.");
        }

        // Exactly the listed files, nothing hidden in the archive.
        foreach (var entry in contents.PayloadPaths)
            if (!listed.Contains(entry))
                return Reject(UpdateErrorCodes.InvalidPackage, $"The package contains an unlisted file '{entry}'.");

        foreach (var file in m.Files)
        {
            if (!contents.PayloadPaths.Contains(file.Path))
                return Reject(UpdateErrorCodes.InvalidPackage, $"The package is missing the listed file '{file.Path}'.");

            if (contents.PayloadLength(file.Path) != file.Length)
                return Reject(UpdateErrorCodes.HashMismatch, $"File '{file.Path}' has an unexpected length.");

            string actual;
            try
            {
                using var stream = contents.OpenPayload(file.Path);
                actual = Sha256Hex.Compute(stream);
            }
            catch (InvalidDataException)
            {
                return Reject(UpdateErrorCodes.InvalidPackage, $"File '{file.Path}' is corrupt.");
            }

            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return Reject(UpdateErrorCodes.HashMismatch, $"File '{file.Path}' does not match its signed hash.");
        }

        if (!string.Equals(PayloadDigest.Compute(m.Files), m.PayloadHash, StringComparison.Ordinal))
            return Reject(UpdateErrorCodes.HashMismatch, "The payload hash does not match the file list.");

        return null;
    }

    // ---------------------------------------------------------------- compatibility / dependencies

    private Result<VerifiedPackage>? CheckCoreUpdate(PackageManifest m, ModuleVersion candidate, IReadOnlyList<InstalledModule> installed)
    {
        // Installed modules must still support the new core version.
        foreach (var module in installed)
        {
            if (candidate < module.MinimumPlatformVersion)
                return Reject(UpdateErrorCodes.Incompatible,
                    $"Module '{module.Id}' requires core {module.MinimumPlatformVersion} or newer; the package provides {candidate}.");

            if (module.MaximumPlatformVersion is not null && candidate > module.MaximumPlatformVersion)
                return Reject(UpdateErrorCodes.Incompatible,
                    $"Module '{module.Id}' supports core versions up to {module.MaximumPlatformVersion}; the package provides {candidate}.");
        }

        foreach (var d in m.Dependencies)
        {
            TryParseDependency(d, out var dependency);
            var present = installed.FirstOrDefault(i => i.Id == dependency!.RequiredModuleId);
            if (present is null)
                return Reject(UpdateErrorCodes.DependencyMissing, $"The core package requires module '{d.ModuleId}' ({d.Operator} {d.Version}), which is not installed.");

            if (!dependency!.IsSatisfiedBy(present.Version))
                return Reject(UpdateErrorCodes.DependencyConflict,
                    $"The core package requires '{d.ModuleId}' {d.Operator} {d.Version}; installed: {present.Version}.", conflict: true);
        }

        return null;
    }

    private Result<VerifiedPackage>? CheckModuleUpdate(
        PackageManifest m, ModuleVersion candidate, IReadOnlyList<InstalledModule> installed, ModuleVersion host)
    {
        // Compatibility with the host is already checked via Min/MaxHostVersion; build the post-update module set.
        var manifests = new List<IModuleManifest>();
        foreach (var module in installed)
            if (!string.Equals(module.Id.Value, m.TargetId, StringComparison.Ordinal))
                manifests.Add(new SyntheticManifest(module.Id, module.Version, module.Dependencies,
                    module.MinimumPlatformVersion, module.MaximumPlatformVersion, module.SchemaVersion));

        var deps = new List<ModuleDependency>();
        foreach (var d in m.Dependencies)
        {
            TryParseDependency(d, out var dependency);
            deps.Add(dependency!);
        }

        if (deps.Any(d => d.RequiredModuleId.Value == m.TargetId))
            return Reject(UpdateErrorCodes.DependencyConflict, "A module cannot depend on itself.", conflict: true);

        manifests.Add(new SyntheticManifest(new ModuleId(m.TargetId), candidate, deps,
            new ModuleVersion(0, 0, 0), null, m.Migration?.ToSchemaVersion ?? 0));

        var result = Resolver.Resolve(manifests);
        if (result.IsSuccess) return null;

        var message = string.Join(" ", result.Errors);
        if (message.Contains("is not present", StringComparison.Ordinal))
            return Reject(UpdateErrorCodes.DependencyMissing, message);

        return Reject(UpdateErrorCodes.DependencyConflict, message, conflict: true);
    }

    private Result<VerifiedPackage>? CheckLicense(PackageManifest m)
    {
        foreach (var module in m.RequiredModuleEntitlements ?? [])
            if (string.IsNullOrWhiteSpace(module) || !licensing.IsModuleLicensed(new ModuleId(module)))
                return Reject(UpdateErrorCodes.LicenseRequired,
                    $"The license does not entitle module '{module}' (license state: {licensing.State}).");

        foreach (var feature in m.RequiredFeatureEntitlements ?? [])
            if (string.IsNullOrWhiteSpace(feature) || !licensing.IsFeatureLicensed(new FeatureId(feature)))
                return Reject(UpdateErrorCodes.LicenseRequired,
                    $"The license does not entitle feature '{feature}' (license state: {licensing.State}).");

        return null;
    }

    private Result<VerifiedPackage>? CheckMigration(PackageManifest m, IReadOnlyList<InstalledModule> installed)
    {
        if (m.Migration is null) return null;

        if (m.PackageType != PackageType.Module)
            return Reject(UpdateErrorCodes.InvalidMigration, "Only module packages may carry migration metadata (modules own their schema).");

        var mig = m.Migration;
        if (mig.FromSchemaVersion < 0 || mig.ToSchemaVersion < mig.FromSchemaVersion)
            return Reject(UpdateErrorCodes.InvalidMigration, "Migration schema versions are invalid (schemas never move backwards).");

        var current = installed.FirstOrDefault(i => string.Equals(i.Id.Value, m.TargetId, StringComparison.Ordinal));
        if (current is not null && current.SchemaVersion > mig.ToSchemaVersion)
            return Reject(UpdateErrorCodes.InvalidMigration,
                $"The installed schema version {current.SchemaVersion} is newer than the package's {mig.ToSchemaVersion}.");

        if (current is not null && current.SchemaVersion != mig.FromSchemaVersion && current.SchemaVersion < mig.FromSchemaVersion)
            return Reject(UpdateErrorCodes.InvalidMigration,
                $"The package migrates from schema {mig.FromSchemaVersion} but the installed schema is {current.SchemaVersion}.");

        return null;
    }

    private Result<VerifiedPackage> Reject(string code, string message, bool conflict = false)
    {
        logger.LogWarning("Package rejected [{Code}]: {Message}", code, message);

        // The code says WHY (unsigned, untrusted key, bad signature, hash mismatch, downgrade, incompatible, license...). The message is the
        // verifier's own text about the package; nothing from inside a rejected package other than that is recorded.
        _ = events.TryRecordAsync(SecurityEvent.Create(
            "security.update.rejected", SecurityEventOutcome.Denied, subjectType: "update", summary: $"[{code}] {message}"));
        return Result.Failure<VerifiedPackage>(conflict ? Error.Conflict(code, message) : Error.Validation(code, message));
    }

    /// <summary>A manifest view used only to run the pure in-memory dependency resolver.</summary>
    private sealed class SyntheticManifest(
        ModuleId id, ModuleVersion version, IReadOnlyList<ModuleDependency> dependencies,
        ModuleVersion minPlatform, ModuleVersion? maxPlatform, int schema) : IModuleManifest
    {
        public ModuleId ModuleId { get; } = id;
        public string Name => ModuleId.Value;
        public ModuleVersion Version { get; } = version;
        public string Publisher => "update-verifier";
        public ModuleVersion MinimumPlatformVersion { get; } = minPlatform;
        public ModuleVersion? MaximumPlatformVersion { get; } = maxPlatform;
        public IReadOnlyList<ModuleDependency> Dependencies { get; } = dependencies;
        public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures => [];
        public int DatabaseSchemaVersion { get; } = schema;
    }
}
