using Client.Updater.Application;
using Client.Updater.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Tests.Common.Network;
using Updates.Contracts;

namespace Updater.Tests;

/// <summary>
/// Stage 12 - the update server failing in every way the network can fail, through the REAL HTTP client against the REAL in-process
/// update server. A failed check or download changes nothing on disk (no partial package, no half-installed module), never throws into
/// the application, and the same update installs normally once the server is reachable again.
/// </summary>
[Trait("Category", "Failure")]
public sealed class UpdateServerOutageTests : IDisposable
{
    private readonly UpdateWorld _w = new();
    private readonly string _packages;
    private readonly WebApplicationFactory<Program> _server;
    private readonly FaultInjectingHandler _faults;
    private readonly UpdateService _service;

    public UpdateServerOutageTests()
    {
        _packages = Path.Combine(_w.Dir, "server-packages");
        Directory.CreateDirectory(_packages);
        var published = _w.PublishModule("catalog", "1.3.0");
        File.Copy(published, Path.Combine(_packages, Path.GetFileName(published)), overwrite: true);

        _server = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["UpdateServer:PackageDirectory"] = _packages }));
        });
        _faults = new FaultInjectingHandler(_server.Server.CreateHandler());
        var http = new HttpClient(_faults) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromSeconds(60) };
        _service = new UpdateService(_w.Store, _w.PackageVerifier, new HttpUpdateClient(http), _w.Installed, _w.Migrations, _w.Safeguard, _w.Options, _w.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateService>.Instance);
    }

    public void Dispose()
    {
        _server.Dispose();
        _w.Dispose();
    }

    private void AssertNoPackageFilesLeftBehind()
        => Assert.Empty(Directory.Exists(_w.Store.DownloadsDir) ? Directory.GetFiles(_w.Store.DownloadsDir) : []);

    public static IEnumerable<object[]> Faults() => Enum.GetValues<NetworkFault>().Where(f => f is not (NetworkFault.None or NetworkFault.HangUntilTimeout)).Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task CheckingForUpdates_IsANonFatalResult_WhenTheServerFails_AndWorksAgainOnceItReturns(NetworkFault fault)
    {
        _faults.Fault = fault;

        var failed = await _service.CheckForUpdatesAsync();

        Assert.True(failed.IsFailure);
        Assert.Equal(UpdateErrorCodes.ServerUnavailable, failed.Error.Code);
        Assert.DoesNotContain("   at ", failed.Error.Description);
        Assert.Null(_w.Store.ReadActive("catalog"));
        AssertNoPackageFilesLeftBehind();

        _faults.Fault = NetworkFault.None;
        var recovered = await _service.CheckForUpdatesAsync();
        Assert.True(recovered.IsSuccess, recovered.IsFailure ? recovered.Error.ToString() : null);
        Assert.Single(recovered.Value);
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task ADownloadFailure_LeavesNoPartialPackageAndNothingInstalled_AndTheRetrySucceeds(NetworkFault fault)
    {
        var updates = await _service.CheckForUpdatesAsync();
        var update = Assert.Single(updates.Value);

        _faults.Fault = fault;
        var failed = await _service.DownloadAsync(update);

        Assert.True(failed.IsFailure);
        AssertNoPackageFilesLeftBehind();                          // no half-downloaded or unverified package is left behind
        Assert.Null(_w.Store.ReadActive("catalog"));               // nothing was installed
        Assert.DoesNotContain("   at ", failed.Error.Description);

        // Recovery: the retry downloads, verifies and installs the valid update, exactly once.
        _faults.Fault = NetworkFault.None;
        var download = await _service.DownloadAsync(update);
        Assert.True(download.IsSuccess, download.IsFailure ? download.Error.ToString() : null);
        var installed = await _service.InstallAsync(download.Value);
        Assert.True(installed.IsSuccess, installed.IsFailure ? installed.Error.ToString() : null);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);
    }

    [Fact]
    public async Task AServerThatNeverAnswers_IsEndedByTheClientTimeout_AsAnUnavailableServer()
    {
        // the one real timeout: the call is ended by HttpClient.Timeout itself (the outcome does not depend on how slow the machine is)
        var hang = new FaultInjectingHandler { Fault = NetworkFault.HangUntilTimeout };
        var client = new HttpUpdateClient(new HttpClient(hang) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromMilliseconds(150) });

        var check = await client.CheckAsync(new UpdateCheckRequest("1.0.0", "net10.0", []));
        var download = await client.DownloadAsync(Guid.NewGuid(), new MemoryStream());

        Assert.Equal(UpdateErrorCodes.ServerUnavailable, check.ErrorCode);
        Assert.True(download.IsFailure);
    }
}
