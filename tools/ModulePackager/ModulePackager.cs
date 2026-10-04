using Security.Es256;
using Updates.Contracts;
using Updates.Package;

namespace Tools.ModulePackager;

/// <summary>Everything the packager needs to describe a package (no signature, no hash yet).</summary>
public sealed record PackageSpec(
    PackageType PackageType,
    string TargetId,
    string Version,
    string MinimumHostVersion,
    string TargetFramework,
    string Publisher,
    string PayloadDirectory,
    string? MaximumHostVersion = null,
    IReadOnlyList<PackageDependency>? Dependencies = null,
    IReadOnlyList<string>? RequiredModuleEntitlements = null,
    IReadOnlyList<string>? RequiredFeatureEntitlements = null,
    PackageMigration? Migration = null);

/// <summary>A validated, hashed but UNSIGNED package description. Only drafts can be published.</summary>
public sealed class PackageDraft
{
    internal PackageDraft(PackageSpec spec, IReadOnlyList<PackageFile> files)
    {
        Spec = spec;
        Files = files;
    }

    public PackageSpec Spec { get; }
    public IReadOnlyList<PackageFile> Files { get; }
}

public sealed record DraftResult(PackageDraft? Draft, IReadOnlyList<string> Errors)
{
    public bool IsSuccess => Draft is not null;
}

/// <summary>
/// Validates a package specification and its payload directory, then hashes every file.
/// It rejects (does not sign, does not silently skip):
///   invalid module identity / version / host version range / dependencies, migration metadata on non-module packages,
///   empty payloads, scripts and installers, native executables in module packages, symbolic links / reparse points,
///   unsafe file names, and anything that violates the shared ManifestRules.
/// Only files that exist in the payload directory become installable, and each is hashed here.
/// </summary>
public static class ModulePackager
{
    public const int MaxFiles = 5_000;

    public static DraftResult CreateDraft(PackageSpec spec)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(spec.PayloadDirectory) || !Directory.Exists(spec.PayloadDirectory))
        {
            return new DraftResult(null, ["The payload directory does not exist."]);
        }

        var root = Path.GetFullPath(spec.PayloadDirectory);
        var files = new List<PackageFile>();

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                errors.Add($"'{Path.GetRelativePath(root, path)}' is a symbolic link/reparse point, which is not allowed.");
                continue;
            }

            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (files.Count >= MaxFiles)
            {
                errors.Add($"The payload has more than {MaxFiles} files.");
                break;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            files.Add(new PackageFile(relative, Sha256Hex.Compute(stream), info.Length));
        }

        if (files.Count == 0)
            errors.Add("The payload directory contains no files.");

        // Validate with the same rules the client applies, using a provisional manifest (hash/id/key are filled in at publish time).
        var provisional = ToManifest(spec, files, Guid.NewGuid(), "provisional", DateTimeOffset.UtcNow);
        var violation = ManifestRules.ValidateStructure(provisional);
        if (violation is not null)
            errors.Add(violation.Message);

        return errors.Count > 0
            ? new DraftResult(null, errors)
            : new DraftResult(new PackageDraft(spec, files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList()), []);
    }

    /// <summary>Builds the final manifest for a draft. PayloadHash covers the file list.</summary>
    public static PackageManifest ToManifest(PackageSpec spec, IReadOnlyList<PackageFile> files, Guid packageId, string keyId, DateTimeOffset createdAt)
        => new(
            SchemaVersion: PackageManifest.CurrentSchemaVersion,
            PackageId: packageId,
            PackageType: spec.PackageType,
            TargetId: spec.TargetId,
            Version: spec.Version,
            MinimumHostVersion: spec.MinimumHostVersion,
            MaximumHostVersion: spec.MaximumHostVersion,
            TargetFramework: spec.TargetFramework,
            Dependencies: spec.Dependencies ?? [],
            RequiredModuleEntitlements: spec.RequiredModuleEntitlements ?? [],
            RequiredFeatureEntitlements: spec.RequiredFeatureEntitlements ?? [],
            Migration: spec.Migration,
            Publisher: spec.Publisher,
            CreatedAt: createdAt,
            Files: files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            PayloadHash: PayloadDigest.Compute(files),
            KeyId: keyId);
}
