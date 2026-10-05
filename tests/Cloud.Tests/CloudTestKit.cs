using AdminPortal.Application;
using BackupServer.Application;
using Cloud.Contracts.Admin;
using Cloud.Infrastructure;
using LicenseServer.Application;
using Licensing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Security.Es256;
using Security.Es256.Signing;
using Tools.ModulePackager;
using UpdateServer.Application;
using Updates.Contracts;
using Updates.Package;
using Packager = Tools.ModulePackager.ModulePackager;
using UpdatePub = Tools.UpdatePublisher.UpdatePublisher;

namespace Cloud.Tests;

public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// A complete Stage 9 server on a throw-away directory: the REAL composition (SQLite server database, file stores, services),
/// built with the same extension methods the hosts use. Nothing is mocked.
/// </summary>
public sealed class CloudWorld : IDisposable
{
    public const string AdminKey = "gpa_test-admin-key";
    public const string PublisherKeyId = "pub-1";
    public static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cloud-tests-" + Guid.NewGuid().ToString("N"));
    public ManualClock Clock { get; } = new(Start);
    public Es256Signer PublisherKey { get; } = Es256Signer.GenerateEphemeral(PublisherKeyId);
    public AdminActor Actor { get; } = new("tester");
    public ServiceProvider Services { get; }
    public Dictionary<string, string?> Settings { get; }

    public string PackageDirectory => Path.Combine(Dir, "packages");
    public string BackupDirectory => Path.Combine(Dir, "backups");
    public string DatabaseFile => Path.Combine(Dir, "cloud.db");

    public CloudWorld(Dictionary<string, string?>? overrides = null, bool trustPublisher = false)
    {
        Directory.CreateDirectory(Dir);
        Settings = DefaultSettings(Dir);
        foreach (var (k, v) in overrides ?? []) Settings[k] = v;

        if (trustPublisher)
        {
            Settings["UpdateServer:TrustedKeys:0:KeyId"] = PublisherKeyId;
            Settings["UpdateServer:TrustedKeys:0:PublicKey"] = PublisherKey.ExportPublicKey();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddCloudDatabase(configuration);
        services.AddAdminPortalServices(configuration);

        Services = services.BuildServiceProvider();
        foreach (var hosted in Services.GetServices<IHostedService>())
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public static Dictionary<string, string?> DefaultSettings(string dir) => new()
    {
        ["CloudDatabase:ConnectionString"] = $"Data Source={Path.Combine(dir, "cloud.db")};Pooling=False",
        ["UpdateServer:PackageDirectory"] = Path.Combine(dir, "packages"),
        ["BackupServer:StorageDirectory"] = Path.Combine(dir, "backups"),
        ["AdminPortal:Keys:0:Name"] = "tester",
        ["AdminPortal:Keys:0:Sha256"] = AdminKeys.Hash(AdminKey)
    };

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public CustomerAdminService Customers => Get<CustomerAdminService>();
    public LicenseAdminService Licenses => Get<LicenseAdminService>();
    public ModuleRegistryService Modules => Get<ModuleRegistryService>();
    public PackageAdminService Packages => Get<PackageAdminService>();
    public AdminOperationsService Operations => Get<AdminOperationsService>();
    public BackupService Backups => Get<BackupService>();
    public BackupAccessService BackupAccess => Get<BackupAccessService>();
    public ILicenseRepository LicenseRepository => Get<ILicenseRepository>();

    // ---- scenario helpers ---------------------------------------------------------------------------------------

    public async Task<CustomerDto> NewCustomerAsync(string name = "Acme Retail")
    {
        var r = await Customers.CreateAsync(Actor, new CustomerRequest(name, "Pat Doe", "pat@acme.test", "555-0100", null));
        Assert.True(r.IsSuccess, r.Error?.Message);
        return r.Value!;
    }

    public async Task<ModuleDto> NewModuleAsync(string id, string category = "Standard")
    {
        var r = await Modules.RegisterAsync(Actor, new RegisterModuleRequest(id, id.ToUpperInvariant(), "test module", category));
        Assert.True(r.IsSuccess, r.Error?.Message);
        return r.Value!;
    }

    /// <summary>Creates a customer, registers the modules and creates a license. Returns the response with the one-time key.</summary>
    public async Task<CreateLicenseResponse> NewLicenseAsync(
        string[]? modules = null, string[]? features = null, string customerName = "Acme Retail", TimeSpan? validFor = null)
    {
        var customer = await NewCustomerAsync(customerName);
        foreach (var m in modules ?? [])
            if ((await Modules.GetAsync(m)).Error is not null)
                await NewModuleAsync(m);

        var r = await Licenses.CreateAsync(Actor, new CreateLicenseRequest(
            customer.Id.ToString(), "genericpos", Clock.GetUtcNow(), Clock.GetUtcNow() + (validFor ?? TimeSpan.FromDays(365)), modules, features));
        Assert.True(r.IsSuccess, r.Error?.Message);
        return r.Value!;
    }

    /// <summary>Activates a license on a fresh installation through the real issuance service. Returns the installation ID.</summary>
    public async Task<Guid> ActivateAsync(CreateLicenseResponse license, Guid? installationId = null)
    {
        var installation = installationId ?? Guid.NewGuid();
        var issuance = new LicenseIssuanceService(LicenseRepository, TestSigner, Clock, new LicenseServerOptions(TimeSpan.FromDays(30), TimeSpan.FromDays(7), "tests"));
        var r = await issuance.ActivateAsync(new ActivationRequest(license.ActivationKey, installation, "genericpos"));
        Assert.True(r.IsSuccess, r.ErrorMessage);
        return installation;
    }

    public ILicenseSigner TestSigner { get; } = new TestLicenseSigner();

    public LicenseIssuanceService Issuance()
        => new(LicenseRepository, TestSigner, Clock, new LicenseServerOptions(TimeSpan.FromDays(30), TimeSpan.FromDays(7), "tests"));

    // ---- packages -----------------------------------------------------------------------------------------------

    public string MakePackage(string target, string version, PackageType type = PackageType.Module, Es256Signer? signer = null,
        string[]? requiredModules = null, Guid? packageId = null)
    {
        var payload = Path.Combine(Dir, "payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "module.dll"), $"binary {target} {version}");

        var spec = new PackageSpec(type, target, version, "1.0.0", "net10.0", "Test Publisher", payload,
            RequiredModuleEntitlements: requiredModules);
        var draft = Packager.CreateDraft(spec);
        Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors));

        var path = Path.Combine(Dir, "out", $"{target}-{version}-{Guid.NewGuid():N}{PackageFormat.Extension}");
        var result = UpdatePub.Publish(draft.Draft!, signer ?? PublisherKey, path, packageId: packageId);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        return path;
    }

    public async Task<Cloud.Contracts.ServiceResult<PackageDto>> PublishAsync(string packagePath, string? notes = null)
    {
        await using var stream = File.OpenRead(packagePath);
        return await Packages.PublishAsync(Actor, stream, notes);
    }

    public async Task<PackageDto> PublishOkAsync(string target, string version, PackageType type = PackageType.Module)
    {
        if (type == PackageType.Module && (await Modules.GetAsync(target)).Error is not null)
            await NewModuleAsync(target);

        var r = await PublishAsync(MakePackage(target, version, type));
        Assert.True(r.IsSuccess, r.Error?.Message);
        return r.Value!;
    }

    public void Dispose()
    {
        Services.Dispose();
        try { Directory.Delete(Dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class TestLicenseSigner : ILicenseSigner, IDisposable
{
    private readonly Es256Signer _signer = Es256Signer.GenerateEphemeral("license-test-key");
    public string KeyId => _signer.KeyId;
    public string Algorithm => LicenseSigning.Algorithm;
    public byte[] Sign(byte[] data) => _signer.Sign(data);
    public string PublicKey => _signer.ExportPublicKey();
    public void Dispose() => _signer.Dispose();
}

internal static class ResultAssert
{
    public static T Ok<T>(Cloud.Contracts.ServiceResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    public static void Fails<T>(Cloud.Contracts.ServiceResult<T> result, string code)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
    }

    public static void Fails(Cloud.Contracts.ServiceResult result, string code)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
    }
}
