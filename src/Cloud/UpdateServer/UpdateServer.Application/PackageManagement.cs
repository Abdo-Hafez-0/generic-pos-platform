using Cloud.Contracts;
using Security.Es256;
using Updates.Contracts;
using Updates.Package;

namespace UpdateServer.Application;

/// <summary>Whether discovery may offer a package. Withdrawn packages stay on record but are never offered or downloadable.</summary>
public enum PackageStatus
{
    Published = 1,
    Withdrawn = 2
}

/// <summary>A package under administration: what discovery serves plus the vendor-side bookkeeping.</summary>
public sealed record ManagedPackage(
    PublishedPackage Package,
    string KeyId,
    string Publisher,
    PackageStatus Status,
    string? ReleaseNotes,
    DateTimeOffset PublishedAt,
    string PublishedBy,
    DateTimeOffset? WithdrawnAt);

/// <summary>Durable package metadata (the bytes live in an <see cref="IPackageFileStore"/>).</summary>
public interface IPackageCatalog
{
    Task<ManagedPackage?> FindAsync(Guid packageId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ManagedPackage>> ListAsync(string? targetId = null, PackageStatus? status = null, CancellationToken cancellationToken = default);

    /// <summary>True when the same target + version + framework is already catalogued (in any status).</summary>
    Task<bool> VersionExistsAsync(string targetId, string version, string targetFramework, CancellationToken cancellationToken = default);

    Task AddAsync(ManagedPackage package, CancellationToken cancellationToken = default);

    /// <summary>Returns false when the package is unknown.</summary>
    Task<bool> SetStatusAsync(Guid packageId, PackageStatus status, DateTimeOffset at, CancellationToken cancellationToken = default);
}

/// <summary>An uploaded package waiting in the staging area (not yet visible to anyone).</summary>
public sealed record StagedFile(string Path, long SizeBytes);

/// <summary>Package bytes: staging, promotion to the permanent location, and reading.</summary>
public interface IPackageFileStore
{
    /// <summary>Copies <paramref name="input"/> to the staging area; returns null (and stores nothing) if it exceeds <paramref name="maxBytes"/>.</summary>
    Task<StagedFile?> StageAsync(Stream input, long maxBytes, CancellationToken cancellationToken = default);

    /// <summary>Moves a staged file to its permanent place as the bytes of <paramref name="packageId"/>.</summary>
    void Commit(StagedFile staged, Guid packageId);

    void Discard(StagedFile staged);

    /// <summary>Removes the permanent file of a package (used to undo a failed publish).</summary>
    void Delete(Guid packageId);

    Stream? OpenRead(Guid packageId);
}

/// <summary>Trusted PUBLIC keys used to reject mis-signed uploads early. Empty = no server-side signature check (clients always verify).</summary>
public sealed record PackageSigningPolicy(IReadOnlyList<TrustedPublicKey> TrustedKeys);

/// <summary>A structurally valid, integrity-checked package ready to be catalogued.</summary>
public sealed record InspectedPackage(PackageManifest Manifest, SignedPackageManifest Envelope, long SizeBytes, string Sha256);

/// <summary>
/// The publication gate. Applies the same structural rules as the packaging tools and the client verifier
/// (<see cref="ManifestRules"/>), checks that the payload matches the manifest, and - when trusted keys are configured -
/// that the signature verifies. It holds no private keys and does not replace client-side verification.
/// </summary>
public sealed class PackageInspector(PackageSigningPolicy policy)
{
    public ServiceResult<InspectedPackage> Inspect(string path)
    {
        var opened = PackageReader.Open(path);
        if (!opened.IsSuccess)
            return Invalid(opened.ErrorMessage ?? "The file is not a valid package.");

        using var contents = opened.Contents!;
        var envelope = contents.Envelope;

        byte[] manifestBytes, signature;
        try
        {
            manifestBytes = Convert.FromBase64String(envelope.Manifest);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            return Invalid("The manifest envelope is malformed.");
        }

        if (policy.TrustedKeys.Count > 0)
        {
            using var verifier = new Es256Verifier(policy.TrustedKeys);
            var check = verifier.Verify(envelope.KeyId, envelope.Algorithm, manifestBytes, signature);
            if (check != SignatureCheck.Valid)
                return ServiceResult<InspectedPackage>.Fail(CloudErrorCodes.PackageSignatureRejected,
                    $"The package signature was rejected ({check}).");
        }

        var manifest = PackageManifestSerializer.TryParseManifest(manifestBytes);
        if (manifest is null)
            return Invalid("The manifest is malformed.");

        if (!string.Equals(manifest.KeyId, envelope.KeyId, StringComparison.Ordinal))
            return Invalid("The manifest key ID does not match the envelope.");

        var violation = ManifestRules.ValidateStructure(manifest);
        if (violation is not null)
            return Invalid(violation.Message);

        var payload = CheckPayload(manifest, contents);
        if (payload is not null)
            return Invalid(payload);

        string sha;
        using (var stream = File.OpenRead(path))
            sha = Sha256Hex.Compute(stream);

        return ServiceResult<InspectedPackage>.Ok(new InspectedPackage(manifest, envelope, new FileInfo(path).Length, sha));
    }

    private static string? CheckPayload(PackageManifest m, PackageContents contents)
    {
        var listed = m.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in contents.PayloadPaths)
            if (!listed.Contains(entry))
                return $"The package contains an unlisted file '{entry}'.";

        foreach (var file in m.Files)
        {
            if (!contents.PayloadPaths.Contains(file.Path))
                return $"The package is missing the listed file '{file.Path}'.";

            if (contents.PayloadLength(file.Path) != file.Length)
                return $"File '{file.Path}' has an unexpected length.";

            string actual;
            try
            {
                using var stream = contents.OpenPayload(file.Path);
                actual = Sha256Hex.Compute(stream);
            }
            catch (InvalidDataException)
            {
                return $"File '{file.Path}' is corrupt.";
            }

            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                return $"File '{file.Path}' does not match its manifest hash.";
        }

        return string.Equals(PayloadDigest.Compute(m.Files), m.PayloadHash, StringComparison.Ordinal)
            ? null
            : "The payload hash does not match the file list.";
    }

    private static ServiceResult<InspectedPackage> Invalid(string message)
        => ServiceResult<InspectedPackage>.Fail(CloudErrorCodes.InvalidPackage, message);
}

/// <summary>Numeric "Major.Minor.Patch" comparison used when listing or ranking published versions.</summary>
public static class PackageVersions
{
    public static bool TryParse(string? text, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3 || !parts.All(p => int.TryParse(p, out var n) && n >= 0)) return false;

        version = (int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 0, parts.Length > 2 ? int.Parse(parts[2]) : 0);
        return true;
    }

    public static int Compare(string a, string b)
    {
        TryParse(a, out var x);
        TryParse(b, out var y);
        return x.CompareTo(y);
    }
}
