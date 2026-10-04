using System.Net;
using System.Net.Http.Json;
using Client.Updater.Application;
using Client.Updater.Domain;
using Client.Updater.Http;
using Client.Updater.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Application.Abstractions.Licensing;
using Platform.Application.Modules;
using Platform.Core.Licensing;
using Platform.Core.Modules;
using Tools.ModulePackager;
using UpdateServer.Application;
using Updates.Contracts;
using Updates.Package;

namespace Updater.Tests;

public sealed class OfflineBehaviourTests : IDisposable
{
    private readonly UpdateWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Fact]
    public async Task UpdateServerUnavailable_Discovery_IsANonFatalResult_NotAnException()
    {
        _w.Client.Unavailable = true;

        var result = await _w.Service.CheckForUpdatesAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(UpdateErrorCodes.ServerUnavailable, result.Error.Code);
        Assert.Equal(1, _w.Client.Calls);
    }

    [Fact]
    public async Task UpdateServerUnavailable_ExistingInstallationAndRecovery_ContinueToWork()
    {
        await _w.Service.InstallAsync(_w.PublishModule("catalog", "1.3.0"));
        await _w.Service.ConfirmHealthyAsync("catalog");
        _w.Client.Unavailable = true;

        var check = await _w.Service.CheckForUpdatesAsync();
        var recovered = await _w.Service.RecoverAsync();

        Assert.True(check.IsFailure);
        Assert.Empty(recovered);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);    // existing module keeps its version
    }

    [Fact]
    public async Task DownloadFailure_LeavesTheInstallationIntact_AndNoPartialFileBehind()
    {
        var info = _w.ToUpdateInfo(_w.PublishModule("catalog", "1.3.0"));
        _w.Client.FailDownload = true;

        var result = await _w.Service.DownloadAsync(info);

        Assert.True(result.IsFailure);
        Assert.Equal(UpdateErrorCodes.DownloadFailed, result.Error.Code);
        Assert.Empty(Directory.GetFiles(_w.Store.DownloadsDir));
        Assert.Null(_w.Store.ReadActive("catalog"));
    }

    [Fact]
    public async Task DownloadWhileOffline_Fails_WithoutThrowing()
    {
        var info = _w.ToUpdateInfo(_w.PublishModule("catalog", "1.3.0"));
        _w.Client.Unavailable = true;

        var result = await _w.Service.DownloadAsync(info);

        Assert.True(result.IsFailure);
        Assert.Equal(UpdateErrorCodes.DownloadFailed, result.Error.Code);
        Assert.Empty(Directory.GetFiles(_w.Store.DownloadsDir));
    }

    [Fact]
    public async Task DownloadedFileThatDoesNotMatchTheAdvertisedHash_IsDeleted()
    {
        var info = _w.ToUpdateInfo(_w.PublishModule("catalog", "1.3.0")) with { DownloadSha256 = new string('0', 64) };

        var result = await _w.Service.DownloadAsync(info);

        Assert.Equal(UpdateErrorCodes.HashMismatch, result.Error.Code);
        Assert.Empty(Directory.GetFiles(_w.Store.DownloadsDir));
    }

    [Fact]
    public async Task Download_ThenInstall_Works_AndRecordsTheJournal()
    {
        var info = _w.ToUpdateInfo(_w.PublishModule("catalog", "1.3.0"));

        var path = await _w.Service.DownloadAsync(info);
        var installed = await _w.Service.InstallAsync(path.Value);

        Assert.True(path.IsSuccess);
        Assert.True(installed.IsSuccess);
        Assert.Contains(installed.Value.History, h => h.State == UpdateState.Downloaded);
    }

    // ------------------------------------------------------------------ discovery filtering

    [Fact]
    public async Task Discovery_OnlyOffersVerifiedInstallableUpdates()
    {
        var good = _w.ToUpdateInfo(_w.PublishModule("catalog", "1.3.0"));
        using var stranger = Security.Es256.Signing.Es256Signer.GenerateEphemeral("stranger");
        var untrusted = _w.ToUpdateInfo(_w.Publish(_w.ModuleSpec("inventory", "1.1.0", _w.PayloadDir("u")), stranger, "u.gpkg"));
        var unlicensed = _w.ToUpdateInfo(_w.PublishModule("pos", "1.1.0", s => s with { RequiredModuleEntitlements = ["ghost"] }));
        var sameVersion = _w.ToUpdateInfo(_w.PublishModule("sales", "1.0.0"));
        var needsNewerHost = _w.ToUpdateInfo(_w.PublishModule("catalog", "1.4.0", s => s with { MinimumHostVersion = "9.0.0" }));
        _w.Client.Updates.AddRange([good, untrusted, unlicensed, sameVersion, needsNewerHost]);

        var result = await _w.Service.CheckForUpdatesAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(["catalog 1.3.0"], result.Value.Select(u => $"{u.TargetId} {u.Version}").ToArray());
    }

    [Fact]
    public async Task Discovery_RejectsMetadataThatContradictsTheSignedManifest()
    {
        var info = _w.ToUpdateInfo(_w.PublishModule("catalog", "1.3.0")) with { Version = "9.9.9" };
        _w.Client.Updates.Add(info);

        var result = await _w.Service.CheckForUpdatesAsync();

        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task Discovery_SendsOnlyVersions_NeverCustomerData()
    {
        var captured = new CapturingClient();
        var service = new UpdateService(_w.Store, _w.PackageVerifier, captured, _w.Installed, _w.Migrations, _w.Safeguard, _w.Options, _w.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateService>.Instance);

        await service.CheckForUpdatesAsync();

        var request = captured.Request!;
        Assert.Equal("1.0.0", request.HostVersion);
        Assert.Equal("net10.0", request.TargetFramework);
        Assert.Contains(request.Installed, i => i is { TargetId: "core", Version: "1.0.0" });
        Assert.Contains(request.Installed, i => i is { TargetId: "pos", Version: "1.0.0" });
        Assert.Equal(5, request.Installed.Count);
    }

    private sealed class CapturingClient : IUpdateClient
    {
        public UpdateCheckRequest? Request { get; private set; }
        public Task<UpdateCheckResponse> CheckAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(UpdateCheckResponse.Success([]));
        }

        public Task<Platform.Core.Results.Result> DownloadAsync(Guid packageId, Stream destination, CancellationToken cancellationToken = default)
            => Task.FromResult<Platform.Core.Results.Result>(Platform.Core.Results.Error.Failure("x", "n/a"));
    }

    // ------------------------------------------------------------------ host integration

    private IServiceProvider BuildHost(string root, IUpdateClient? client = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Updater:UpdateRoot"] = root,
            ["Updater:TrustedKeys:0:KeyId"] = UpdateWorld.KeyId,
            ["Updater:TrustedKeys:0:PublicKey"] = _w.Signer.ExportPublicKey(),
            ["Database:DatabaseFolder"] = "Custom",
            ["Database:CustomFolderPath"] = Path.Combine(_w.Dir, "db")
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IModuleRegistry, ModuleRegistry>();
        services.AddSingleton<ILicenseEntitlementService>(_w.Entitlements);
        if (client is not null) services.AddSingleton(client);
        new UpdaterHostingModule().RegisterServices(new HostBuilderContext(new Dictionary<object, object>()) { Configuration = config }, services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task HostingModule_RegistersTheUpdater_AndStartupRecoveryNeverTouchesTheNetwork()
    {
        var client = new FakeUpdateClient { Unavailable = true };
        var provider = BuildHost(Path.Combine(_w.Dir, "host-updates"), client);

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);   // must complete although the server is "down"

        Assert.NotNull(provider.GetRequiredService<IUpdateService>());
        Assert.NotNull(provider.GetRequiredService<PackageVerifier>());
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task HostingModule_WithoutATransport_ReportsDiscoveryUnavailable_AndStartsFine()
    {
        var provider = BuildHost(Path.Combine(_w.Dir, "host-updates2"));

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);
        var result = await provider.GetRequiredService<IUpdateService>().CheckForUpdatesAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(UpdateErrorCodes.ServerUnavailable, result.Error.Code);
    }

    [Fact]
    public async Task HostingModule_TrustedKeysFromConfiguration_VerifyRealPackages()
    {
        var provider = BuildHost(Path.Combine(_w.Dir, "host-updates3"));
        var package = _w.PublishModule("catalog", "1.3.0");

        var result = provider.GetRequiredService<PackageVerifier>().VerifyPackage(package);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StartupRecovery_FromTheHostedService_RollsBackAnUnconfirmedUpdate()
    {
        var root = Path.Combine(_w.Dir, "host-updates4");
        var provider = BuildHost(root);
        var store = provider.GetRequiredService<UpdateStore>();
        store.EnsureDirectories();
        store.WriteActive("catalog", new ActivePointer { Version = "1.3.0" });
        store.SaveJournal(new UpdateJournal
        {
            PackageId = Guid.NewGuid(), PackageType = PackageType.Module, TargetId = "catalog", Version = "1.3.0",
            State = UpdateState.Activated, StartupAttempts = 2, History = [new UpdateStateEntry(UpdateState.Activated, DateTimeOffset.UtcNow, null)]
        });

        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        Assert.Null(store.ReadActive("catalog"));   // third start without confirmation: rolled back
    }

    [Theory]
    [InlineData("https://updates.example.com/", true)]
    [InlineData("http://localhost:5000/", true)]
    [InlineData("http://127.0.0.1:5000/", true)]
    [InlineData("http://updates.example.com/", false)]
    [InlineData("ftp://updates.example.com/", false)]
    [InlineData("", false)]
    [InlineData("junk", false)]
    public void UpdateServerAddress_RequiresHttps_ExceptLoopback(string url, bool accepted)
        => Assert.Equal(accepted, UpdateHttpExtensions.ValidateBaseAddress(url) is not null);

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no route to host");
    }

    [Fact]
    public async Task HttpClient_NetworkFailures_BecomeResults()
    {
        var client = new HttpUpdateClient(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://down.test/") });

        var check = await client.CheckAsync(new UpdateCheckRequest("1.0.0", "net10.0", []));
        var download = await client.DownloadAsync(Guid.NewGuid(), new MemoryStream());

        Assert.False(check.IsSuccess);
        Assert.Equal(UpdateErrorCodes.ServerUnavailable, check.ErrorCode);
        Assert.True(download.IsFailure);
    }
}

/// <summary>The real ASP.NET Core update server hosted in-process and driven through the real HTTP client.</summary>
public sealed class UpdateServerIntegrationTests : IDisposable
{
    private readonly UpdateWorld _w = new();
    private readonly string _packages;

    public UpdateServerIntegrationTests()
    {
        _packages = Path.Combine(_w.Dir, "server-packages");
        Directory.CreateDirectory(_packages);
    }

    public void Dispose() => _w.Dispose();

    private WebApplicationFactory<Program> StartServer()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["UpdateServer:PackageDirectory"] = _packages }));
        });

    private string PublishToServer(string module, string version, Func<PackageSpec, PackageSpec>? tweak = null)
    {
        var path = _w.PublishModule(module, version, tweak);
        File.Copy(path, Path.Combine(_packages, Path.GetFileName(path)), overwrite: true);
        return Path.Combine(_packages, Path.GetFileName(path));
    }

    private UpdateService ClientFor(HttpClient http)
        => new(_w.Store, _w.PackageVerifier, new HttpUpdateClient(http), _w.Installed, _w.Migrations, _w.Safeguard, _w.Options, _w.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateService>.Instance);

    [Fact]
    public async Task Scenario_ValidModuleUpdate_Discover_Download_Verify_Install_Confirm()
    {
        PublishToServer("catalog", "1.3.0");
        await using var server = StartServer();
        var service = ClientFor(server.CreateClient());

        var updates = await service.CheckForUpdatesAsync();
        Assert.True(updates.IsSuccess, updates.IsFailure ? updates.Error.ToString() : null);
        var update = Assert.Single(updates.Value);
        Assert.Equal("catalog", update.TargetId);

        var download = await service.DownloadAsync(update);
        Assert.True(download.IsSuccess, download.IsFailure ? download.Error.ToString() : null);

        var installed = await service.InstallAsync(download.Value);
        Assert.True(installed.IsSuccess, installed.IsFailure ? installed.Error.ToString() : null);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);

        Assert.True((await service.ConfirmHealthyAsync("catalog")).IsSuccess);
        Assert.Equal(UpdateState.Confirmed, _w.Store.LoadJournal(update.PackageId)!.State);
    }

    [Fact]
    public async Task Server_OffersOnlyNewerPackages_ForInstalledTargets_AndTheNewestOne()
    {
        PublishToServer("catalog", "1.0.0");   // same as installed: not offered
        PublishToServer("catalog", "1.2.0");
        PublishToServer("catalog", "1.3.0");   // newest: offered
        PublishToServer("accounting", "1.0.0"); // not installed on the client: not offered
        await using var server = StartServer();
        var service = ClientFor(server.CreateClient());

        var updates = await service.CheckForUpdatesAsync();

        Assert.Equal(["catalog 1.3.0"], updates.Value.Select(u => $"{u.TargetId} {u.Version}").ToArray());
    }

    [Fact]
    public async Task Scenario_TamperedPackageOnTheServer_IsRejected_AndTheInstalledVersionRemains()
    {
        var onServer = PublishToServer("catalog", "1.3.0");
        await using var server = StartServer();
        var service = ClientFor(server.CreateClient());
        var update = (await service.CheckForUpdatesAsync()).Value.Single();

        // The package bytes on the server are modified after discovery advertised them.
        var tamperedPath = UpdateWorld.Repack(onServer, e => e["payload/module.dll"][0] ^= 1);
        File.Copy(tamperedPath, onServer, overwrite: true);

        var download = await service.DownloadAsync(update);   // advisory file hash no longer matches

        Assert.True(download.IsFailure);
        Assert.Equal(UpdateErrorCodes.HashMismatch, download.Error.Code);
        Assert.Null(_w.Store.ReadActive("catalog"));
    }

    [Fact]
    public async Task Scenario_TamperedPackage_WithTheAdvisoryHashUpdatedToo_IsStillRejectedBySignatureAndHashChecks()
    {
        PublishToServer("catalog", "1.3.0");
        await using var server = StartServer();
        var http = server.CreateClient();
        var service = ClientFor(http);
        var update = (await service.CheckForUpdatesAsync()).Value.Single();
        var downloaded = (await service.DownloadAsync(update)).Value;

        // An attacker who controls the download path swaps the file for a modified one.
        var tampered = UpdateWorld.Repack(downloaded, e => e["payload/module.dll"][0] ^= 1);
        var result = await service.InstallAsync(tampered);

        Assert.True(result.IsFailure);
        Assert.Equal(UpdateErrorCodes.HashMismatch, result.Error.Code);
        Assert.Null(_w.Store.ReadActive("catalog"));
    }

    [Fact]
    public async Task Server_UnknownPackage_Is404_AndBadRequest_Is400()
    {
        await using var server = StartServer();
        using var http = server.CreateClient();

        var missing = await http.GetAsync($"api/updates/packages/{Guid.NewGuid()}");
        var bad = await http.PostAsJsonAsync("api/updates/check", new UpdateCheckRequest("junk", "", []), PackageManifestSerializer.Options);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Server_IgnoresFilesThatAreNotValidPackages_AndHoldsNoSigningKeys()
    {
        File.WriteAllText(Path.Combine(_packages, "junk.gpkg"), "not a package");
        File.WriteAllText(Path.Combine(_packages, "notes.txt"), "hello");
        PublishToServer("catalog", "1.3.0");
        await using var server = StartServer();
        var repository = server.Services.GetRequiredService<IPackageRepository>();

        Assert.Single(repository.List());
        var asm = typeof(UpdateDiscoveryService).Assembly.GetReferencedAssemblies().Select(a => a.Name!);
        Assert.DoesNotContain(asm, n => n.Contains("Signing", StringComparison.Ordinal));   // the server cannot sign anything
    }

    [Fact]
    public void DiscoveryService_RespectsHostRuntimeAndVersionRules()
    {
        PublishToServer("catalog", "1.3.0", s => s with { MinimumHostVersion = "1.1.0" });
        var repository = new DirectoryPackageRepository(_packages);
        var discovery = new UpdateDiscoveryService(repository);
        var installed = new[] { new InstalledTarget("catalog", "1.0.0") };

        var tooOld = discovery.Check(new UpdateCheckRequest("1.0.0", "net10.0", installed));
        var ok = discovery.Check(new UpdateCheckRequest("1.1.0", "net10.0", installed));
        var wrongRuntime = discovery.Check(new UpdateCheckRequest("1.1.0", "net8.0", installed));
        var invalid = discovery.Check(new UpdateCheckRequest("", "net10.0", installed));

        Assert.Empty(tooOld.Updates);
        Assert.Single(ok.Updates);
        Assert.Empty(wrongRuntime.Updates);
        Assert.False(invalid.IsSuccess);
    }

    [Fact]
    public async Task Server_ServesTheExactPackageBytes()
    {
        var onServer = PublishToServer("catalog", "1.3.0");
        await using var server = StartServer();
        using var http = server.CreateClient();
        var id = UpdateWorld.ReadManifest(onServer).PackageId;

        var bytes = await http.GetByteArrayAsync($"api/updates/packages/{id}");

        Assert.Equal(File.ReadAllBytes(onServer), bytes);
    }
}
