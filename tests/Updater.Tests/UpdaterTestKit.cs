using System.IO.Compression;
using System.Text;
using Client.Updater.Application;
using Client.Updater.Domain;
using Client.Updater.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Licensing;
using Platform.Core.Licensing;
using Platform.Core.Modules;
using Platform.Core.Results;
using Security.Es256;
using Security.Es256.Signing;
using Tools.ModulePackager;
using Tools.UpdatePublisher;
using Updates.Contracts;
using Updates.Package;

namespace Updater.Tests;

public sealed class FakeManifestModule
{
    public static InstalledModule Module(string id, string version, string[]? depends = null, int schema = 1, string minPlatform = "1.0.0")
        => new(new ModuleId(id), ModuleVersion.Parse(version),
            (depends ?? []).Select(d => new ModuleDependency(new ModuleId(d), VersionRange.AtLeast(new ModuleVersion(1, 0, 0)))).ToList(),
            ModuleVersion.Parse(minPlatform), null, schema);
}

/// <summary>Mutable stand-in for what the host reports as installed.</summary>
public sealed class FakeInstalledState : IInstalledStateProvider
{
    public ModuleVersion HostVersion { get; set; } = new(1, 0, 0);
    public List<InstalledModule> Modules { get; } = [];
    public IReadOnlyList<InstalledModule> GetInstalledModules() => Modules;
}

/// <summary>Stand-in for ILicenseEntitlementService (the platform abstraction the updater consults).</summary>
public sealed class StubEntitlements(LicenseState state = LicenseState.Active, params string[] modules) : ILicenseEntitlementService
{
    public LicenseState State { get; set; } = state;
    public HashSet<string> Modules { get; } = [.. modules];
    public HashSet<string> Features { get; } = [];

    private bool Grants => State is LicenseState.Active or LicenseState.GracePeriod;
    public bool IsModuleLicensed(ModuleId moduleId) => Grants && Modules.Contains(moduleId.Value);
    public bool IsFeatureLicensed(FeatureId featureId) => Grants && Features.Contains(featureId.Value);
}

public sealed class FakeSafeguard : IDataSafeguard
{
    public bool FailCreate { get; set; }
    public bool FailRestore { get; set; }
    public List<string> RestorePoints { get; } = [];
    public List<string> Restored { get; } = [];

    public Task<Result<string>> CreateRestorePointAsync(Guid packageId, CancellationToken cancellationToken = default)
    {
        if (FailCreate) return Task.FromResult(Result.Failure<string>(Error.Failure("x", "disk full")));
        var point = "restore-" + packageId.ToString("N");
        RestorePoints.Add(point);
        return Task.FromResult(Result.Success(point));
    }

    public Task<Result> RestoreAsync(string restorePoint, CancellationToken cancellationToken = default)
    {
        if (FailRestore) return Task.FromResult(Result.Failure(Error.Failure("x", "restore failed")));
        Restored.Add(restorePoint);
        return Task.FromResult(Result.Success());
    }
}

public sealed class FakeMigrations : IMigrationCoordinator
{
    public MigrationOutcome Outcome { get; set; } = new(true, true, false, null);
    public Action? OnMigrate { get; set; }
    public List<MigrationRequest> Requests { get; } = [];

    public Task<MigrationOutcome> MigrateAsync(MigrationRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        OnMigrate?.Invoke();
        return Task.FromResult(Outcome);
    }
}

public sealed class FakeUpdateClient : IUpdateClient
{
    public Dictionary<Guid, byte[]> Packages { get; } = [];
    public List<UpdateInfo> Updates { get; } = [];
    public bool Unavailable { get; set; }
    public bool FailDownload { get; set; }
    public int Calls { get; private set; }

    public Task<UpdateCheckResponse> CheckAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Unavailable) throw new HttpRequestException("network down");
        return Task.FromResult(UpdateCheckResponse.Success(Updates.ToList()));
    }

    public async Task<Result> DownloadAsync(Guid packageId, Stream destination, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Unavailable) throw new HttpRequestException("network down");
        if (!Packages.TryGetValue(packageId, out var bytes)) return Error.NotFound("x", "unknown package");
        if (FailDownload)
        {
            await destination.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), cancellationToken);   // truncated transfer
            return Error.Failure("x", "connection lost");
        }

        await destination.WriteAsync(bytes, cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// A complete updater environment on a temp directory: a publisher key, a trusting client, fakes for host state,
/// licensing, migrations and restore points, and the REAL verifier, store and update service.
/// </summary>
public sealed class UpdateWorld : IDisposable
{
    public const string KeyId = "pub-1";

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "updater-tests-" + Guid.NewGuid().ToString("N"));
    public Es256Signer Signer { get; } = Es256Signer.GenerateEphemeral(KeyId);
    public FakeInstalledState Installed { get; } = new();
    public StubEntitlements Entitlements { get; } = new(LicenseState.Active, "catalog", "inventory", "sales", "pos", "accounting");
    public FakeSafeguard Safeguard { get; } = new();
    public FakeMigrations Migrations { get; } = new();
    public FakeUpdateClient Client { get; } = new();
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    public UpdateStore Store { get; }
    public UpdaterOptions Options { get; } = new("net10.0", MaxStartupAttempts: 2);
    public Es256Verifier Verifier { get; private set; } = null!;
    public PackageVerifier PackageVerifier { get; private set; } = null!;
    public UpdateService Service { get; private set; } = null!;

    public UpdateWorld(params TrustedPublicKey[]? extraTrusted)
    {
        Directory.CreateDirectory(Dir);
        Store = new UpdateStore(Path.Combine(Dir, "updates"));
        Installed.Modules.AddRange([
            FakeManifestModule.Module("catalog", "1.0.0"),
            FakeManifestModule.Module("inventory", "1.0.0", ["catalog"]),
            FakeManifestModule.Module("sales", "1.0.0", ["catalog", "inventory"]),
            FakeManifestModule.Module("pos", "1.0.0", ["catalog", "inventory", "sales"])]);

        Rebuild([Signer.ToTrustedKey(), .. extraTrusted ?? []]);
    }

    public void Rebuild(IEnumerable<TrustedPublicKey> trusted, ILicenseEntitlementService? entitlements = null)
    {
        Verifier = new Es256Verifier(trusted);
        PackageVerifier = new PackageVerifier(Verifier, Installed, entitlements ?? Entitlements, Options, NullLogger<PackageVerifier>.Instance);
        Service = new UpdateService(Store, PackageVerifier, Client, Installed, Migrations, Safeguard, Options, Clock,
            NullLogger<UpdateService>.Instance);
    }

    public string PayloadDir(string name, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(Dir, "payload-" + name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        foreach (var (path, content) in files.Length > 0 ? files : [("module.dll", "binary-" + name), ("config/settings.json", "{}")])
        {
            var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return dir;
    }

    public PackageSpec ModuleSpec(string module, string version, string payloadDir) => new(
        PackageType.Module, module, version, "1.0.0", "net10.0", "Test Publisher", payloadDir);

    public PackageSpec CoreSpec(string version, string payloadDir) => new(
        PackageType.Core, "core", version, "1.0.0", "net10.0", "Test Publisher", payloadDir);

    /// <summary>Validates, signs and writes a package. Throws if the spec is invalid (use the packager directly to test rejection).</summary>
    public string Publish(PackageSpec spec, Es256Signer? signer = null, string? fileName = null, Guid? packageId = null)
    {
        var draft = ModulePackager.CreateDraft(spec);
        Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors));
        var path = Path.Combine(Dir, "out", fileName ?? $"{spec.TargetId}-{spec.Version}{PackageFormat.Extension}");
        var result = UpdatePublisher.Publish(draft.Draft!, signer ?? Signer, path, packageId: packageId);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        return path;
    }

    public string PublishModule(string module, string version, Func<PackageSpec, PackageSpec>? tweak = null, string? payloadName = null)
    {
        var payload = PayloadDir(payloadName ?? $"{module}-{version}");
        var spec = ModuleSpec(module, version, payload);
        return Publish(tweak is null ? spec : tweak(spec));
    }

    public UpdateInfo ToUpdateInfo(string packagePath)
    {
        var opened = PackageReader.Open(packagePath);
        using var contents = opened.Contents!;
        var manifest = PackageManifestSerializer.TryParseManifest(Convert.FromBase64String(contents.Envelope.Manifest))!;
        using var stream = File.OpenRead(packagePath);
        var hash = Sha256Hex.Compute(stream);
        Client.Packages[manifest.PackageId] = File.ReadAllBytes(packagePath);
        return new UpdateInfo(manifest.PackageId, manifest.PackageType, manifest.TargetId, manifest.Version,
            new FileInfo(packagePath).Length, hash, contents.Envelope);
    }

    public void Dispose()
    {
        Verifier.Dispose();
        Signer.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- tampering helpers

    /// <summary>Rewrites a package's entries (name -> bytes), letting a test change, add or remove entries.</summary>
    public static string Repack(string packagePath, Action<Dictionary<string, byte[]>> mutate, string? suffix = null)
    {
        var entries = new Dictionary<string, byte[]>();
        using (var zip = ZipFile.OpenRead(packagePath))
            foreach (var entry in zip.Entries)
            {
                using var ms = new MemoryStream();
                using var s = entry.Open();
                s.CopyTo(ms);
                entries[entry.FullName] = ms.ToArray();
            }

        mutate(entries);

        var output = Path.Combine(Path.GetDirectoryName(packagePath)!, Path.GetFileNameWithoutExtension(packagePath) + (suffix ?? "-tampered") + PackageFormat.Extension);
        if (File.Exists(output)) File.Delete(output);
        using var outZip = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var e = outZip.CreateEntry(name);
            using var w = e.Open();
            w.Write(bytes);
        }

        return output;
    }

    public static SignedPackageManifest ReadEnvelope(string packagePath)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        using var reader = new StreamReader(zip.GetEntry(PackageFormat.ManifestEntry)!.Open(), Encoding.UTF8);
        return PackageManifestSerializer.TryParseEnvelope(reader.ReadToEnd())!;
    }

    public static PackageManifest ReadManifest(string packagePath)
        => PackageManifestSerializer.TryParseManifest(Convert.FromBase64String(ReadEnvelope(packagePath).Manifest))!;

    /// <summary>Signs an arbitrary (possibly invalid) manifest with a key, bypassing the packager's validation. For negative tests only.</summary>
    public static string WriteSignedPackage(string path, PackageManifest manifest, Es256Signer signer, params (string Path, string Content)[] payload)
    {
        var bytes = PackageManifestSerializer.SerializeManifestBytes(manifest);
        var envelope = new SignedPackageManifest(
            PackageManifestSerializer.ToManifestText(bytes), signer.KeyId, signer.Algorithm, Convert.ToBase64String(signer.Sign(bytes)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        PackageWriter.Write(fs, envelope, payload.Select(p => new PackageFileSource(p.Path, () => new MemoryStream(Encoding.UTF8.GetBytes(p.Content)))));
        return path;
    }

    public static PackageManifest ManifestFor(string target, string version, PackageType type = PackageType.Module,
        (string Path, string Content)[]? files = null, Action<List<PackageFile>>? tweakFiles = null)
    {
        var list = (files ?? [("module.dll", "x")])
            .Select(f => new PackageFile(f.Path, Sha256Hex.Compute(Encoding.UTF8.GetBytes(f.Content)), Encoding.UTF8.GetByteCount(f.Content))).ToList();
        tweakFiles?.Invoke(list);
        return new PackageManifest(1, Guid.NewGuid(), type, target, version, "1.0.0", null, "net10.0", [], [], [], null,
            "Test Publisher", DateTimeOffset.UtcNow, list, PayloadDigest.Compute(list), KeyId);
    }
}

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
