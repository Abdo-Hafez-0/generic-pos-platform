using System.Net;
using System.Net.Sockets;
using Client.Licensing.Application;
using Client.Licensing.Domain;
using Client.Licensing.Http;
using Client.Licensing.Infrastructure;
using Licensing.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LicenseServer.Application;
using LicenseServer.Infrastructure;
using Platform.Core.Licensing;
using Platform.Core.Modules;
using Tests.Common.Network;

namespace Licensing.Tests;

/// <summary>
/// Stage 12 - the license server failing in every way the network can fail, through the REAL HTTP client against the REAL in-process
/// license API. Whatever the cloud does: no exception reaches the licensing logic, the locally stored license keeps its state, offline
/// evaluation keeps working, and when the server returns a renewal succeeds without duplicating anything.
/// </summary>
[Trait("Category", "Failure")]
public sealed class LicenseServerOutageTests : IClassFixture<LicenseServerOutageTests.ApiFactory>
{
    private static int _nextKey;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const int Keys = 64;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = new Dictionary<string, string?>();
                for (var i = 0; i < Keys; i++)
                {
                    values[$"LicenseServer:DevLicenses:{i}:ActivationKey"] = $"OUTAGE-KEY-{i}";
                    values[$"LicenseServer:DevLicenses:{i}:CustomerId"] = "outage-customer";
                    values[$"LicenseServer:DevLicenses:{i}:ProductId"] = "genericpos";
                    values[$"LicenseServer:DevLicenses:{i}:ValidFrom"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
                    values[$"LicenseServer:DevLicenses:{i}:ValidUntil"] = DateTimeOffset.UtcNow.AddYears(1).ToString("O");
                    values[$"LicenseServer:DevLicenses:{i}:Modules:0"] = "pos";
                    values[$"LicenseServer:DevLicenses:{i}:Features:0"] = "advancedreports";
                }
                config.AddInMemoryCollection(values);
            });
        }
    }

    private readonly ApiFactory _factory;

    public LicenseServerOutageTests(ApiFactory factory) => _factory = factory;

    /// <summary>A licensed client whose HTTP calls pass through a fault injector in front of the real server.</summary>
    private sealed class OutageClient
    {
        public required FaultInjectingHandler Faults { get; init; }
        public required InMemoryLicenseStore Store { get; init; }
        public required Func<LicenseService> NewService { get; init; }
        public string Key { get; init; } = "";
    }

    private OutageClient NewClient()
    {
        var key = $"OUTAGE-KEY-{(Interlocked.Increment(ref _nextKey) - 1) % ApiFactory.Keys}";
        var signer = (EcdsaLicenseSigner)_factory.Services.GetRequiredService<ILicenseSigner>();
        var verifier = new EcdsaLicenseVerifier([new TrustedLicenseKey(signer.KeyId, signer.ExportPublicKey())]);
        var faults = new FaultInjectingHandler(_factory.Server.CreateHandler());
        var store = new InMemoryLicenseStore();
        var identity = new InMemoryIdentityStore();
        var http = new HttpClient(faults) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromMilliseconds(400) };

        return new OutageClient
        {
            Faults = faults, Store = store, Key = key,
            NewService = () => new LicenseService(
                new InstallationIdentityService(identity, TimeProvider.System), store, verifier,
                new HttpLicenseClient(http), TimeProvider.System, new LicensingOptions("genericpos"), new LicensePolicy())
        };
    }

    public static IEnumerable<object[]> Faults() => Enum.GetValues<NetworkFault>().Where(f => f != NetworkFault.None).Select(f => new object[] { f });

    private static bool IsTransportFault(NetworkFault fault) => fault is NetworkFault.NoNetwork or NetworkFault.DnsFailure or NetworkFault.ConnectionRefused
        or NetworkFault.ConnectionTimeout or NetworkFault.RequestTimeout or NetworkFault.TlsCertificateFailure or NetworkFault.ConnectionResetDuringBody;

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task Activation_FailsCleanly_AndStoresNothing_WhenTheLicenseServerFails(NetworkFault fault)
    {
        var client = NewClient();
        client.Faults.Fault = fault;
        var service = client.NewService();

        var result = await service.ActivateAsync(client.Key);

        Assert.True(result.IsFailure);
        Assert.Null(client.Store.Stored);
        Assert.Equal(LicenseState.Unlicensed, service.State);
        if (IsTransportFault(fault))
            Assert.Equal(LicenseErrorCodes.ServerUnreachable, result.Error.Code);
        Assert.DoesNotContain("   at ", result.Error.Description);   // no stack trace reaches the user

        // Recovery: the same key activates normally once the server is back.
        client.Faults.Fault = NetworkFault.None;
        var recovered = await client.NewService().ActivateAsync(client.Key);
        Assert.True(recovered.IsSuccess, recovered.IsFailure ? recovered.Error.ToString() : null);
        Assert.Equal(LicenseState.Active, recovered.Value.State);
    }

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task ARenewalFailure_LeavesTheLocalLicenseUntouched_AndOfflineEvaluationKeepsWorking(NetworkFault fault)
    {
        var client = NewClient();
        var activated = await client.NewService().ActivateAsync(client.Key);
        Assert.True(activated.IsSuccess, activated.IsFailure ? activated.Error.ToString() : null);
        var storedBefore = client.Store.Stored;
        var versionBefore = activated.Value.Payload!.LicenseVersion;

        client.Faults.Fault = fault;
        var restarted = client.NewService();   // "application restart" with the cloud down
        var evaluation = await restarted.InitializeAsync();
        var renewal = await restarted.RenewAsync();

        Assert.Equal(LicenseState.Active, evaluation.State);
        Assert.True(renewal.IsFailure);
        Assert.Equal(LicenseState.Active, restarted.State);
        Assert.True(restarted.IsModuleLicensed(new ModuleId("pos")));
        Assert.Same(storedBefore, client.Store.Stored);   // nothing was written or replaced

        // Recovery: renewal works as soon as the server is back, and issues a newer license.
        client.Faults.Fault = NetworkFault.None;
        var renewed = await restarted.RenewAsync();
        Assert.True(renewed.IsSuccess, renewed.IsFailure ? renewed.Error.ToString() : null);
        Assert.True(renewed.Value.Payload!.LicenseVersion > versionBefore);
        Assert.Equal(LicenseState.Active, restarted.State);
    }

    [Fact]
    public async Task EvaluatingTheLicense_NeverCallsTheServer()
    {
        var client = NewClient();
        await client.NewService().ActivateAsync(client.Key);
        var requestsAfterActivation = client.Faults.Requests;

        var restarted = client.NewService();
        await restarted.InitializeAsync();
        _ = restarted.State;
        _ = restarted.IsModuleLicensed(new ModuleId("pos"));

        Assert.Equal(requestsAfterActivation, client.Faults.Requests);
    }

    [Fact]
    public async Task ARealClosedPort_IsReportedAsServerUnreachable_NotAnException()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();   // nothing listens any more: a genuine "connection refused" from the operating system

        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
        var response = await new HttpLicenseClient(http).ActivateAsync(new ActivationRequest("K", Guid.NewGuid(), "genericpos"));

        Assert.False(response.IsSuccess);
        Assert.Equal(LicenseErrorCodes.ServerUnreachable, response.ErrorCode);
    }

    [Fact]
    public async Task AnExpiredLicense_WhileOffline_StaysExpired_AndComesBackAfterARenewalWhenTheServerReturns()
    {
        using var world = new LicensingWorld();
        var down = new SwitchableLicenseClient(world.Client);
        var service = world.NewClientService(client: down);
        Assert.True((await service.ActivateAsync(LicensingWorld.ActivationKey)).IsSuccess);

        down.Down = true;
        world.Clock.Advance(TimeSpan.FromDays(45));   // lease and grace are over while offline
        Assert.Equal(LicenseState.Expired, service.State);
        Assert.False(service.IsModuleLicensed(new ModuleId("pos")));
        Assert.True((await service.RenewAsync()).IsFailure);
        Assert.Equal(LicenseState.Expired, service.State);   // an outage never grants anything

        down.Down = false;
        Assert.True((await service.RenewAsync()).IsSuccess);
        Assert.Equal(LicenseState.Active, service.State);
        Assert.True(service.IsModuleLicensed(new ModuleId("pos")));
    }

    /// <summary>The in-process server behind an on/off switch: "the license server is down / back".</summary>
    private sealed class SwitchableLicenseClient(ILicenseClient inner) : ILicenseClient
    {
        public bool Down { get; set; }

        public Task<ActivationResponse> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
            => Down ? Task.FromResult(ActivationResponse.Failure(LicenseErrorCodes.ServerUnreachable, "down")) : inner.ActivateAsync(request, cancellationToken);

        public Task<RenewalResponse> RenewAsync(RenewalRequest request, CancellationToken cancellationToken = default)
            => Down ? Task.FromResult(RenewalResponse.Failure(LicenseErrorCodes.ServerUnreachable, "down")) : inner.RenewAsync(request, cancellationToken);
    }
}
