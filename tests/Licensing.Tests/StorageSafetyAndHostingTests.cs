using Client.Licensing.Application;
using Client.Licensing.Domain;
using Client.Licensing.Infrastructure;
using Licensing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Licensing;
using Platform.Core.Licensing;
using Platform.Core.Modules;

namespace Licensing.Tests;

public sealed class StorageSafetyAndHostingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "licensing-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private LicenseService NewFileBackedService(LicensingWorld world, ILicenseClient? client = null)
    {
        var options = new LicensingStorageOptions(Path.Combine(_dir, "Licensing"));
        return new LicenseService(
            new InstallationIdentityService(new FileInstallationIdentityStore(options), world.Clock),
            new FileLicenseStore(options),
            world.NewVerifier(),
            client ?? world.Client,
            world.Clock,
            new LicensingOptions(LicensingWorld.ProductId),
            new LicensePolicy());
    }

    // --- File-based stores ---

    [Fact]
    public async Task FileIdentityStore_PersistsIdentity_AcrossInstances()
    {
        var options = new LicensingStorageOptions(Path.Combine(_dir, "Licensing"));
        var first = await new InstallationIdentityService(new FileInstallationIdentityStore(options), TimeProvider.System).GetOrCreateAsync();

        var second = await new InstallationIdentityService(new FileInstallationIdentityStore(options), TimeProvider.System).GetOrCreateAsync();

        Assert.Equal(first.InstallationId, second.InstallationId);
        Assert.True(File.Exists(Path.Combine(_dir, "Licensing", "installation.json")));
    }

    [Fact]
    public async Task FileIdentityStore_CorruptFile_GeneratesNewIdentity_WithoutThrowing()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Licensing"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "Licensing", "installation.json"), "{ not json");
        var options = new LicensingStorageOptions(Path.Combine(_dir, "Licensing"));

        var identity = await new InstallationIdentityService(new FileInstallationIdentityStore(options), TimeProvider.System).GetOrCreateAsync();

        Assert.True(identity.IsValid);
    }

    [Fact]
    public async Task FileStores_ActivationSurvivesRestart_AndWorksOffline()
    {
        using var world = new LicensingWorld();
        var activated = await NewFileBackedService(world).ActivateAsync(LicensingWorld.ActivationKey);
        Assert.True(activated.IsSuccess);
        Assert.True(File.Exists(Path.Combine(_dir, "Licensing", "license.json")));

        var offline = new UnreachableLicenseClient();
        var restarted = NewFileBackedService(world, offline);
        var evaluation = await restarted.InitializeAsync();

        Assert.Equal(LicenseState.Active, evaluation.State);
        Assert.True(restarted.IsModuleLicensed(new ModuleId("pos")));
        Assert.Equal(0, offline.Calls);
    }

    [Fact]
    public async Task FileLicenseStore_EditedOnDisk_IsRejected()
    {
        using var world = new LicensingWorld();
        await NewFileBackedService(world).ActivateAsync(LicensingWorld.ActivationKey);
        var path = Path.Combine(_dir, "Licensing", "license.json");
        var stored = LicenseSerializer.TryDeserialize(await File.ReadAllTextAsync(path))!;
        var forged = LicenseSerializer.TryParsePayload(stored.Payload)! with { Modules = ["pos", "accounting", "multibranch"] };
        await File.WriteAllTextAsync(path, LicenseSerializer.Serialize(
            stored with { Payload = LicenseSerializer.ToPayloadText(LicenseSerializer.SerializePayloadBytes(forged)) }));

        var evaluation = await NewFileBackedService(world).InitializeAsync();

        Assert.Equal(LicenseState.Invalid, evaluation.State);
        Assert.Equal(InvalidReason.BadSignature, evaluation.InvalidReason);
    }

    [Fact]
    public async Task FileLicenseStore_GarbageFile_IsInvalid_NotACrash()
    {
        using var world = new LicensingWorld();
        Directory.CreateDirectory(Path.Combine(_dir, "Licensing"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "Licensing", "license.json"), "garbage");

        var evaluation = await NewFileBackedService(world).InitializeAsync();

        Assert.Equal(LicenseState.Invalid, evaluation.State);
    }

    [Fact]
    public async Task FileLicenseStore_NoFile_IsUnlicensed()
    {
        using var world = new LicensingWorld();

        Assert.Equal(LicenseState.Unlicensed, (await NewFileBackedService(world).InitializeAsync()).State);
    }

    [Fact]
    public async Task FileLicenseStore_Save_ReplacesAtomically_AndLeavesNoTempFiles()
    {
        var options = new LicensingStorageOptions(Path.Combine(_dir, "Licensing"));
        var store = new FileLicenseStore(options);
        await store.SaveAsync(new SignedLicense("a", "k", "ES256", "s1"));
        await store.SaveAsync(new SignedLicense("b", "k", "ES256", "s2"));

        Assert.Equal("b", (await store.LoadAsync())!.License!.Payload);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "Licensing"), "*.tmp"));
    }

    [Fact]
    public void DefaultStorage_IsSeparateFromTheBusinessDatabaseLocation()
    {
        var licensing = LicensingStorageOptions.Default().Directory;

        Assert.EndsWith(Path.Combine("GenericPOS", "Licensing"), licensing);
        Assert.NotEqual("genericpos.db", Path.GetFileName(licensing));
    }

    // --- Expiration safety ---

    [Fact]
    public async Task Expiration_RestrictsAccess_ButBusinessDataRemainsIntact()
    {
        // A stand-in for the customer's business database (the real one is SQLite owned by the business modules).
        var dbPath = Path.Combine(_dir, "business.db");
        Directory.CreateDirectory(_dir);
        await using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE cat_Products (Id TEXT PRIMARY KEY, Name TEXT);
                CREATE TABLE inv_StockItems (Id TEXT PRIMARY KEY, OnHand TEXT);
                CREATE TABLE sal_Sales (Id TEXT PRIMARY KEY, Total TEXT);
                INSERT INTO cat_Products VALUES ('p1','Widget');
                INSERT INTO inv_StockItems VALUES ('s1','7');
                INSERT INTO sal_Sales VALUES ('x1','75');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var sizeBefore = new FileInfo(dbPath).Length;

        using var world = new LicensingWorld();
        var service = NewFileBackedService(world);
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        Assert.True(service.IsModuleLicensed(new ModuleId("pos")));

        // Time passes well beyond lease + grace; renewal impossible (offline); then a suspended + revoked cycle too.
        world.Clock.Advance(TimeSpan.FromDays(400));
        var expiredService = NewFileBackedService(world, new UnreachableLicenseClient());
        var evaluation = await expiredService.InitializeAsync();
        await expiredService.RenewAsync();
        await world.Server.SetStatusAsync(world.Record.LicenseId, LicenseStatusClaim.Revoked);

        // Access is restricted...
        Assert.Equal(LicenseState.Expired, evaluation.State);
        Assert.False(expiredService.IsModuleLicensed(new ModuleId("pos")));

        // ...and all customer data is untouched.
        Assert.True(File.Exists(dbPath));
        Assert.Equal(sizeBefore, new FileInfo(dbPath).Length);
        await using var verify = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await verify.OpenAsync();
        Assert.Equal(1L, await Scalar(verify, "SELECT COUNT(*) FROM cat_Products"));
        Assert.Equal("7", (string)(await Scalar(verify, "SELECT OnHand FROM inv_StockItems"))!);
        Assert.Equal(1L, await Scalar(verify, "SELECT COUNT(*) FROM sal_Sales"));
        Assert.Equal(3L, await Scalar(verify, "SELECT COUNT(*) FROM sqlite_master WHERE type='table'"));
    }

    private static async Task<object?> Scalar(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public void ClientLicensing_HasNoDatabaseOrBusinessModuleAccess_SoItCannotTouchBusinessData()
    {
        var references = typeof(ILicenseService).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("SQLitePCL", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("System.Net.Http", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("Catalog.", StringComparison.Ordinal)
            || n.StartsWith("Inventory.", StringComparison.Ordinal)
            || n.StartsWith("Sales.", StringComparison.Ordinal)
            || n.StartsWith("POS.", StringComparison.Ordinal));
    }

    // --- Host integration ---

    private static IServiceProvider BuildHostServices(string dir, IReadOnlyDictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Licensing:StorageDirectory"] = dir,
            ["Licensing:ProductId"] = LicensingWorld.ProductId
        };
        if (extra is not null) foreach (var kv in extra) values[kv.Key] = kv.Value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new LicensingHostingModule().RegisterServices(new HostBuilderContext(new Dictionary<object, object>()) { Configuration = config }, services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task HostingModule_RegistersServices_AndInitializerStartsOfflineWithoutALicense()
    {
        var provider = BuildHostServices(Path.Combine(_dir, "host"));

        var entitlements = provider.GetRequiredService<ILicenseEntitlementService>();
        Assert.Same(provider.GetRequiredService<ILicenseService>(), entitlements);   // one shared instance, no static state

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        Assert.Equal(LicenseState.Unlicensed, entitlements.State);
        Assert.False(entitlements.IsModuleLicensed(new ModuleId("pos")));
        Assert.True(File.Exists(Path.Combine(_dir, "host", "installation.json")));   // identity created at startup
    }

    [Fact]
    public async Task HostingModule_TrustedKeysFromConfiguration_VerifyLicensesFromThatKey()
    {
        using var world = new LicensingWorld();
        var provider = BuildHostServices(Path.Combine(_dir, "host2"), new Dictionary<string, string?>
        {
            ["Licensing:TrustedKeys:0:KeyId"] = "test-key-1",
            ["Licensing:TrustedKeys:0:PublicKey"] = world.Signer.ExportPublicKey()
        });
        var verifier = provider.GetRequiredService<ILicenseVerifier>();
        var license = (await world.Server.ActivateAsync(new ActivationRequest(LicensingWorld.ActivationKey, Guid.NewGuid(), LicensingWorld.ProductId))).License!;

        Assert.True(verifier.Verify(license).IsValid);
    }

    [Fact]
    public async Task HostingModule_WithNoTransport_ActivationReportsServerNotAvailable_AndEvaluationStillWorks()
    {
        var provider = BuildHostServices(Path.Combine(_dir, "host3"));
        var service = provider.GetRequiredService<ILicenseService>();

        var result = await service.ActivateAsync("ANY");

        Assert.True(result.IsFailure);
        Assert.Equal(LicenseErrorCodes.ServerUnreachable, result.Error.Code);
        Assert.Equal(LicenseState.Unlicensed, service.Current.State);
    }

    [Fact]
    public void HostingModule_NoTrustedKeys_FailsClosed()
    {
        var provider = BuildHostServices(Path.Combine(_dir, "host4"));
        using var anyone = new LicensingWorld();
        var license = new SignedLicense("AAAA", "test-key-1", LicenseSigning.Algorithm, "AAAA");

        Assert.False(provider.GetRequiredService<ILicenseVerifier>().Verify(license).IsValid);
    }
}
