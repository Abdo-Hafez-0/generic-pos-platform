using Security.Es256;
using Updates.Contracts;
using Updates.Package;

namespace UpdateServer.Application;

/// <summary>A published package as the server knows it. The server does not interpret the signature: clients verify it.</summary>
public sealed record PublishedPackage(
    Guid PackageId,
    PackageType PackageType,
    string TargetId,
    string Version,
    string TargetFramework,
    string MinimumHostVersion,
    SignedPackageManifest SignedManifest,
    long SizeBytes,
    string Sha256);

/// <summary>Catalog of published packages and access to their bytes.</summary>
public interface IPackageRepository
{
    IReadOnlyList<PublishedPackage> List();

    PublishedPackage? Find(Guid packageId);

    /// <summary>Opens the package file for reading, or null if unknown.</summary>
    Stream? OpenRead(Guid packageId);
}

/// <summary>
/// Read-only repository over a directory of .gpkg files (development / simple deployments). Files that are not valid
/// packages are ignored. The server holds NO signing keys and trusts nothing: it only serves what it was given.
/// </summary>
public sealed class DirectoryPackageRepository : IPackageRepository
{
    private readonly Dictionary<Guid, (PublishedPackage Package, string Path)> _packages = [];

    public DirectoryPackageRepository(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var path in Directory.EnumerateFiles(directory, "*" + PackageFormat.Extension))
            TryAdd(path);
    }

    public bool TryAdd(string path)
    {
        var opened = PackageReader.Open(path);
        if (!opened.IsSuccess) return false;

        using var contents = opened.Contents!;
        var bytes = TryDecode(contents.Envelope.Manifest);
        var manifest = bytes is null ? null : PackageManifestSerializer.TryParseManifest(bytes);
        if (manifest is null || manifest.PackageId == Guid.Empty) return false;

        string hash;
        using (var stream = File.OpenRead(path))
            hash = Sha256Hex.Compute(stream);

        var package = new PublishedPackage(
            manifest.PackageId, manifest.PackageType, manifest.TargetId, manifest.Version, manifest.TargetFramework,
            manifest.MinimumHostVersion, contents.Envelope, new FileInfo(path).Length, hash);

        lock (_packages) _packages[manifest.PackageId] = (package, path);
        return true;
    }

    public IReadOnlyList<PublishedPackage> List()
    {
        lock (_packages) return _packages.Values.Select(v => v.Package).ToList();
    }

    public PublishedPackage? Find(Guid packageId)
    {
        lock (_packages) return _packages.TryGetValue(packageId, out var v) ? v.Package : null;
    }

    public Stream? OpenRead(Guid packageId)
    {
        string? path;
        lock (_packages) path = _packages.TryGetValue(packageId, out var v) ? v.Path : null;
        return path is null || !File.Exists(path) ? null : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static byte[]? TryDecode(string text)
    {
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }
}

/// <summary>
/// Answers "are there updates for me?". For every target the client has installed (core and modules) it offers the newest
/// published version that is newer than the installed one and built for the client's runtime and host version.
/// It returns METADATA (including the signed manifest) and never forces anything. New modules the client does not have are
/// not offered; the server does not decide compatibility beyond host/runtime - the client verifies everything.
/// </summary>
public sealed class UpdateDiscoveryService(IPackageRepository repository)
{
    public UpdateCheckResponse Check(UpdateCheckRequest request)
    {
        if (request is null || !TryParse(request.HostVersion, out var host) || string.IsNullOrWhiteSpace(request.TargetFramework))
            return UpdateCheckResponse.Failure("Update.InvalidRequest", "A valid host version and target framework are required.");

        var all = repository.List();
        var updates = new List<UpdateInfo>();

        foreach (var installed in request.Installed ?? [])
        {
            if (!TryParse(installed.Version, out var installedVersion)) continue;

            var best = all
                .Where(p => string.Equals(p.TargetId, installed.TargetId, StringComparison.Ordinal)
                         && string.Equals(p.TargetFramework, request.TargetFramework, StringComparison.OrdinalIgnoreCase)
                         && TryParse(p.Version, out var v) && Compare(v, installedVersion) > 0
                         && TryParse(p.MinimumHostVersion, out var minHost) && Compare(host, minHost) >= 0)
                .OrderByDescending(p => Parse(p.Version), Comparer<(int, int, int)>.Create(Compare))
                .FirstOrDefault();

            if (best is not null)
                updates.Add(new UpdateInfo(best.PackageId, best.PackageType, best.TargetId, best.Version, best.SizeBytes, best.Sha256, best.SignedManifest));
        }

        return UpdateCheckResponse.Success(updates);
    }

    private static bool TryParse(string? text, out (int, int, int) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3 || !parts.All(p => int.TryParse(p, out var n) && n >= 0)) return false;
        version = (int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 0, parts.Length > 2 ? int.Parse(parts[2]) : 0);
        return true;
    }

    private static (int, int, int) Parse(string text)
    {
        TryParse(text, out var v);
        return v;
    }

    private static int Compare((int, int, int) a, (int, int, int) b)
        => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1)
         : a.Item2 != b.Item2 ? a.Item2.CompareTo(b.Item2)
         : a.Item3.CompareTo(b.Item3);
}
