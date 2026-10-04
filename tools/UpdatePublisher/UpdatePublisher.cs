using Security.Es256;
using Security.Es256.Signing;
using Tools.ModulePackager;
using Packager = Tools.ModulePackager.ModulePackager;
using Updates.Contracts;
using Updates.Package;

namespace Tools.UpdatePublisher;

public sealed record PublishResult(
    bool IsSuccess,
    string? PackagePath,
    PackageManifest? Manifest,
    string? PackageSha256,
    IReadOnlyList<string> Errors);

/// <summary>
/// The trusted publishing step: draft -> manifest -> signature -> distributable package.
///
/// WHAT IS SIGNED: the exact UTF-8 JSON bytes of the PackageManifest (which lists every payload file's SHA-256/length and the
/// PayloadHash). The private key stays with the <see cref="Es256Signer"/> passed in; the package only receives the
/// signature and the KeyId. Production keys are loaded from a file outside the repository; tests use ephemeral keys.
/// </summary>
public static class UpdatePublisher
{
    public static PublishResult Publish(PackageDraft draft, Es256Signer signer, string outputPath, DateTimeOffset? createdAt = null, Guid? packageId = null)
    {
        var spec = draft.Spec;

        // Re-read and re-hash the payload: the files must still be exactly what was validated.
        var root = Path.GetFullPath(spec.PayloadDirectory);
        var files = new List<PackageFile>();
        foreach (var f in draft.Files)
        {
            var path = Path.Combine(root, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                return Fail($"Payload file '{f.Path}' disappeared after validation.");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Sha256Hex.Compute(stream);
            if (stream.Length != f.Length || !string.Equals(hash, f.Sha256, StringComparison.OrdinalIgnoreCase))
                return Fail($"Payload file '{f.Path}' changed after validation. Create a new draft.");

            files.Add(f);
        }

        var manifest = Packager.ToManifest(spec, files, packageId ?? Guid.NewGuid(), signer.KeyId, createdAt ?? DateTimeOffset.UtcNow);

        // Never sign something the client would reject structurally.
        var violation = ManifestRules.ValidateStructure(manifest);
        if (violation is not null)
            return Fail(violation.Message);

        var manifestBytes = PackageManifestSerializer.SerializeManifestBytes(manifest);
        var signature = signer.Sign(manifestBytes);
        var envelope = new SignedPackageManifest(
            PackageManifestSerializer.ToManifestText(manifestBytes), signer.KeyId, signer.Algorithm, Convert.ToBase64String(signature));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temp = outputPath + ".tmp";
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            PackageWriter.Write(output, envelope, files.Select(f => new PackageFileSource(
                f.Path, () => new FileStream(Path.Combine(root, f.Path.Replace('/', Path.DirectorySeparatorChar)), FileMode.Open, FileAccess.Read, FileShare.Read))));
        }

        File.Move(temp, outputPath, overwrite: true);

        string packageHash;
        using (var read = File.OpenRead(outputPath))
            packageHash = Sha256Hex.Compute(read);

        return new PublishResult(true, outputPath, manifest, packageHash, []);
    }

    private static PublishResult Fail(string error) => new(false, null, null, null, [error]);
}
