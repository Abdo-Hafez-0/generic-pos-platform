using System.IO.Compression;
using System.Text;
using Security.Es256;
using Updates.Contracts;

namespace Updates.Package;

/// <summary>
/// PACKAGE FORMAT (.gpkg, a standard ZIP):
///   manifest.json        the SignedPackageManifest envelope (JSON)
///   payload/&lt;path&gt;       every installable file (exactly the files listed in the manifest)
/// Nothing else is allowed in the archive. There are no scripts and no SQL: packages are controlled content only.
///
/// WHAT IS SIGNED: the exact UTF-8 JSON bytes of the PackageManifest (Base64 in envelope.Manifest).
/// WHAT THE SIGNATURE TRANSITIVELY PROTECTS: the manifest lists the SHA-256 and length of every payload file and a
/// PayloadHash (see <see cref="PayloadDigest"/>), so any change to any payload byte, path or set of files is detected.
/// </summary>
public static class PackageFormat
{
    public const string ManifestEntry = "manifest.json";
    public const string PayloadPrefix = "payload/";
    public const string Extension = ".gpkg";

    public const long MaxPackageBytes = 1L * 1024 * 1024 * 1024;
    public const long MaxEntryBytes = 512L * 1024 * 1024;
    public const long MaxManifestBytes = 1024 * 1024;
    public const int MaxEntries = 10_000;
    public const int MaxCompressionRatio = 200;

    private static readonly string[] ForbiddenScriptExtensions =
        [".ps1", ".psm1", ".bat", ".cmd", ".vbs", ".vbe", ".sh", ".wsf", ".msi", ".reg", ".lnk"];

    /// <summary>True if the relative payload path is safe: no rooted/absolute paths, no "..", no drive letters, no backslashes, no empty segments.</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260) return false;
        if (path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.EndsWith('/')) return false;
        if (path.Any(char.IsControl)) return false;

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..") return false;
            if (segment != segment.TrimEnd(' ', '.')) return false;
        }

        return true;
    }

    /// <summary>
    /// Packages may not carry scripts/installers (no arbitrary execution during update). Module packages additionally
    /// may not carry native executables; only the core package may contain an .exe.
    /// </summary>
    public static bool IsForbiddenFile(string path, PackageType type)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ForbiddenScriptExtensions.Contains(ext)) return true;
        return type == PackageType.Module && ext is ".exe" or ".com" or ".scr";
    }
}

/// <summary>
/// The deterministic content hash of a payload: files sorted by ordinal path, one line per file
/// "{path}\t{sha256}\t{length}\n" (UTF-8), SHA-256 over all lines, lowercase hex.
/// </summary>
public static class PayloadDigest
{
    public static string Compute(IEnumerable<PackageFile> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            sb.Append(f.Path).Append('\t').Append(f.Sha256).Append('\t').Append(f.Length).Append('\n');

        return Sha256Hex.Compute(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}

/// <summary>A payload file to be written into a package.</summary>
public sealed record PackageFileSource(string Path, Func<Stream> Open);

public static class PackageWriter
{
    private static readonly DateTimeOffset FixedTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Writes a deterministic package: manifest first, payload sorted by path, fixed timestamps.</summary>
    public static void Write(Stream output, SignedPackageManifest envelope, IEnumerable<PackageFileSource> payload)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var manifestEntry = zip.CreateEntry(PackageFormat.ManifestEntry, CompressionLevel.Optimal);
        manifestEntry.LastWriteTime = FixedTimestamp;
        using (var w = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
            w.Write(PackageManifestSerializer.SerializeEnvelope(envelope));

        foreach (var file in payload.OrderBy(p => p.Path, StringComparer.Ordinal))
        {
            var entry = zip.CreateEntry(PackageFormat.PayloadPrefix + file.Path, CompressionLevel.Optimal);
            entry.LastWriteTime = FixedTimestamp;
            using var target = entry.Open();
            using var source = file.Open();
            source.CopyTo(target);
        }
    }
}

public sealed record PackageReadResult(PackageContents? Contents, string? ErrorMessage)
{
    public bool IsSuccess => Contents is not null;
}

/// <summary>An opened package: the (UNTRUSTED until verified) envelope and the payload entries. Dispose to release the file.</summary>
public sealed class PackageContents : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _payload;

    internal PackageContents(ZipArchive zip, SignedPackageManifest envelope, Dictionary<string, ZipArchiveEntry> payload)
    {
        _zip = zip;
        Envelope = envelope;
        _payload = payload;
    }

    /// <summary>NOT trusted until its signature has been verified.</summary>
    public SignedPackageManifest Envelope { get; }

    public IReadOnlyCollection<string> PayloadPaths => _payload.Keys;

    public long PayloadLength(string path) => _payload[path].Length;

    public Stream OpenPayload(string path) => _payload[path].Open();

    public void Dispose() => _zip.Dispose();
}

public static class PackageReader
{
    /// <summary>
    /// Opens a package safely: size/entry limits, only manifest.json + payload/*, safe unique names, zip-bomb check.
    /// Nothing is extracted and nothing is trusted yet.
    /// </summary>
    public static PackageReadResult Open(string path)
    {
        if (!File.Exists(path))
            return new PackageReadResult(null, "Package file does not exist.");

        FileStream? stream = null;
        ZipArchive? zip = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length is 0 or > PackageFormat.MaxPackageBytes)
                return new PackageReadResult(null, "Package size is invalid.");

            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            if (zip.Entries.Count > PackageFormat.MaxEntries)
                return Fail(zip, "Package has too many entries.");

            ZipArchiveEntry? manifestEntry = null;
            var payload = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;

            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') && entry.Length == 0) continue; // directory marker

                if (!seen.Add(entry.FullName))
                    return Fail(zip, "Package contains duplicate entries.");

                if (entry.Length > PackageFormat.MaxEntryBytes)
                    return Fail(zip, "Package entry is too large.");

                total += entry.Length;
                if (total > PackageFormat.MaxPackageBytes)
                    return Fail(zip, "Package expands to too much data.");

                if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0
                    && entry.Length / entry.CompressedLength > PackageFormat.MaxCompressionRatio)
                    return Fail(zip, "Package entry has a suspicious compression ratio.");

                if (entry.FullName == PackageFormat.ManifestEntry)
                {
                    manifestEntry = entry;
                }
                else if (entry.FullName.StartsWith(PackageFormat.PayloadPrefix, StringComparison.Ordinal))
                {
                    var relative = entry.FullName[PackageFormat.PayloadPrefix.Length..];
                    if (!PackageFormat.IsSafeRelativePath(relative))
                        return Fail(zip, $"Package contains an unsafe path '{entry.FullName}'.");

                    payload[relative] = entry;
                }
                else
                {
                    return Fail(zip, $"Package contains an unexpected entry '{entry.FullName}'.");
                }
            }

            if (manifestEntry is null || manifestEntry.Length is 0 or > PackageFormat.MaxManifestBytes)
                return Fail(zip, "Package manifest is missing or invalid.");

            string json;
            using (var reader = new StreamReader(manifestEntry.Open(), new UTF8Encoding(false)))
                json = reader.ReadToEnd();

            var envelope = PackageManifestSerializer.TryParseEnvelope(json);
            if (envelope is null
                || string.IsNullOrEmpty(envelope.Manifest) || string.IsNullOrEmpty(envelope.KeyId)
                || string.IsNullOrEmpty(envelope.Algorithm) || string.IsNullOrEmpty(envelope.Signature))
                return Fail(zip, "Package manifest envelope is malformed.");

            return new PackageReadResult(new PackageContents(zip, envelope, payload), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            zip?.Dispose();
            stream?.Dispose();
            return new PackageReadResult(null, "Package cannot be opened: " + ex.Message);
        }
    }

    private static PackageReadResult Fail(ZipArchive zip, string message)
    {
        zip.Dispose();
        return new PackageReadResult(null, message);
    }
}

/// <summary>The outcome of a manifest rule check (the first violated rule).</summary>
public sealed record ManifestViolation(string Code, string Message);

/// <summary>
/// Structural rules for a package manifest, shared by the packaging tools (reject BEFORE signing) and the client verifier
/// (reject BEFORE installing), so both sides apply exactly the same definition of a valid package.
/// </summary>
public static class ManifestRules
{
    public static bool IsValidModuleId(string id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '_');

    public static bool TryParseDependency(PackageDependency d, out string moduleId, out string op, out string version)
    {
        moduleId = d.ModuleId; op = d.Operator; version = d.Version;
        return IsValidModuleId(d.ModuleId)
            && d.Operator is ">=" or "<=" or "==" or ">" or "<"
            && IsVersion(d.Version);
    }

    private static bool IsVersion(string? v) =>
        !string.IsNullOrWhiteSpace(v)
        && v.Split('.').Length is >= 1 and <= 3
        && v.Split('.').All(part => int.TryParse(part, out var n) && n >= 0);

    /// <summary>Identity, versions, dependencies, file list and migration metadata. Does not look at any file content.</summary>
    public static ManifestViolation? ValidateStructure(PackageManifest m)
    {
        if (m.SchemaVersion != PackageManifest.CurrentSchemaVersion)
            return new("Update.Incompatible", $"Package schema version {m.SchemaVersion} is not supported.");

        if (!Enum.IsDefined(m.PackageType))
            return new("Update.InvalidPackage", "The package type is unknown.");

        if (m.PackageId == Guid.Empty)
            return new("Update.InvalidPackage", "The package ID is invalid.");

        if (string.IsNullOrWhiteSpace(m.TargetId) || m.TargetId != m.TargetId.Trim().ToLowerInvariant())
            return new("Update.InvalidPackage", "The package target ID is invalid.");

        if (m.PackageType == PackageType.Core && m.TargetId != PackageManifest.CoreTargetId)
            return new("Update.InvalidPackage", "A core package must target 'core'.");

        if (m.PackageType == PackageType.Module && (m.TargetId == PackageManifest.CoreTargetId || !IsValidModuleId(m.TargetId)))
            return new("Update.InvalidPackage", "A module package must target a valid module ID.");

        if (!IsVersion(m.Version))
            return new("Update.InvalidPackage", "The package version is invalid.");

        if (!IsVersion(m.MinimumHostVersion) || (m.MaximumHostVersion is not null && !IsVersion(m.MaximumHostVersion)))
            return new("Update.InvalidPackage", "The host version range is invalid.");

        if (string.IsNullOrWhiteSpace(m.TargetFramework) || string.IsNullOrWhiteSpace(m.Publisher))
            return new("Update.InvalidPackage", "The package target framework or publisher is missing.");

        foreach (var d in m.Dependencies ?? [])
            if (!TryParseDependency(d, out _, out _, out _))
                return new("Update.InvalidPackage", $"Invalid dependency on '{d.ModuleId}'.");

        if (m.PackageType == PackageType.Module && (m.Dependencies ?? []).Any(d => d.ModuleId == m.TargetId))
            return new("Update.DependencyConflict", "A module cannot depend on itself.");

        foreach (var e in (m.RequiredModuleEntitlements ?? []).Concat(m.RequiredFeatureEntitlements ?? []))
            if (string.IsNullOrWhiteSpace(e))
                return new("Update.InvalidPackage", "An entitlement requirement is empty.");

        if (m.Files is null || m.Files.Count == 0)
            return new("Update.InvalidPackage", "The package contains no files.");

        if (m.Migration is not null)
        {
            if (m.PackageType != PackageType.Module)
                return new("Update.InvalidMigration", "Only module packages may carry migration metadata (modules own their schema).");

            if (m.Migration.FromSchemaVersion < 0 || m.Migration.ToSchemaVersion < m.Migration.FromSchemaVersion)
                return new("Update.InvalidMigration", "Migration schema versions are invalid (schemas never move backwards).");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in m.Files)
        {
            if (!PackageFormat.IsSafeRelativePath(file.Path))
                return new("Update.InvalidPackage", $"The manifest lists an unsafe path '{file.Path}'.");

            if (PackageFormat.IsForbiddenFile(file.Path, m.PackageType))
                return new("Update.InvalidPackage", $"File '{file.Path}' is not allowed in a {m.PackageType} package (scripts/installers/executables are forbidden).");

            if (!seen.Add(file.Path))
                return new("Update.InvalidPackage", $"The manifest lists '{file.Path}' twice.");

            if (file.Length < 0 || string.IsNullOrEmpty(file.Sha256) || file.Sha256.Length != 64)
                return new("Update.InvalidPackage", $"File '{file.Path}' has an invalid hash or length.");
        }

        return null;
    }
}
