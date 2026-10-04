using System.Net;
using System.Net.Http.Json;
using Client.Licensing.Application;
using Client.Licensing.Domain;
using Client.Licensing.Http;
using Client.Licensing.Infrastructure;
using LicenseServer.Application;
using LicenseServer.Infrastructure;
using Licensing.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Licensing;
using Platform.Core.Modules;

namespace Licensing.Tests;

public sealed class ServerIssuanceTests
{
    [Fact]
    public async Task Activate_InvalidRequest_Fails()
    {
        using var world = new LicensingWorld();

        Assert.Equal(LicenseErrorCodes.InvalidRequest,
            (await world.Server.ActivateAsync(new ActivationRequest("", Guid.NewGuid(), "p"))).ErrorCode);
        Assert.Equal(LicenseErrorCodes.InvalidRequest,
            (await world.Server.ActivateAsync(new ActivationRequest("k", Guid.Empty, "p"))).ErrorCode);
        Assert.Equal(LicenseErrorCodes.InvalidRequest,
            (await world.Server.ActivateAsync(new ActivationRequest("k", Guid.NewGuid(), " "))).ErrorCode);
    }

    [Fact]
    public async Task Activate_WrongProduct_Fails()
    {
        using var world = new LicensingWorld();

        var r = await world.Server.ActivateAsync(new ActivationRequest(LicensingWorld.ActivationKey, Guid.NewGuid(), "other-product"));

        Assert.Equal(LicenseErrorCodes.ProductMismatch, r.ErrorCode);
    }

    [Theory]
    [InlineData(LicenseStatusClaim.Revoked, LicenseErrorCodes.Revoked)]
    [InlineData(LicenseStatusClaim.Suspended, LicenseErrorCodes.Suspended)]
    public async Task Activate_RevokedOrSuspendedLicense_IsRefused(LicenseStatusClaim status, string code)
    {
        using var world = new LicensingWorld();
        await world.Server.SetStatusAsync(world.Record.LicenseId, status);

        var r = await world.Server.ActivateAsync(new ActivationRequest(LicensingWorld.ActivationKey, Guid.NewGuid(), LicensingWorld.ProductId));

        Assert.Equal(code, r.ErrorCode);
    }

    [Fact]
    public async Task Renew_Unknown_OrWrongInstallation_Fails()
    {
        using var world = new LicensingWorld();
        var installation = Guid.NewGuid();
        await world.Server.ActivateAsync(new ActivationRequest(LicensingWorld.ActivationKey, installation, LicensingWorld.ProductId));

        Assert.Equal(LicenseErrorCodes.NotFound, (await world.Server.RenewAsync(new RenewalRequest(Guid.NewGuid(), installation, 1))).ErrorCode);
        Assert.Equal(LicenseErrorCodes.InstallationMismatch,
            (await world.Server.RenewAsync(new RenewalRequest(world.Record.LicenseId, Guid.NewGuid(), 1))).ErrorCode);
        Assert.Equal(LicenseErrorCodes.InvalidRequest,
            (await world.Server.RenewAsync(new RenewalRequest(Guid.Empty, installation, 1))).ErrorCode);
    }

    [Fact]
    public async Task Issued_Payload_ContainsEveryRequiredField()
    {
        using var world = new LicensingWorld();
        var installation = Guid.NewGuid();

        var license = (await world.Server.ActivateAsync(new ActivationRequest(LicensingWorld.ActivationKey, installation, LicensingWorld.ProductId))).License!;
        var p = LicenseSerializer.TryParsePayload(license.Payload)!;

        Assert.Equal(world.Record.LicenseId, p.LicenseId);
        Assert.Equal("customer-1", p.CustomerId);
        Assert.Equal(installation, p.InstallationId);
        Assert.Equal(LicensingWorld.ProductId, p.ProductId);
        Assert.Equal(LicensingWorld.Start, p.IssuedAt);
        Assert.Equal(LicenseStatusClaim.Active, p.Status);
        Assert.Contains("pos", p.Modules);
        Assert.Contains("advancedreports", p.Features);
        Assert.Equal("test-key-1", license.KeyId);
        Assert.Equal(LicenseSigning.Algorithm, license.Algorithm);
        Assert.Equal(license.KeyId, p.KeyId);
    }
}

public sealed class HttpClientTests
{
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("no route to host");
    }

    private sealed class StaticHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task NetworkFailure_BecomesUnreachableResponse_NotAnException()
    {
        var client = new HttpLicenseClient(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://licenses.example.test/") });

        var activation = await client.ActivateAsync(new ActivationRequest("k", Guid.NewGuid(), "p"));
        var renewal = await client.RenewAsync(new RenewalRequest(Guid.NewGuid(), Guid.NewGuid(), 1));

        Assert.False(activation.IsSuccess);
        Assert.Equal(LicenseErrorCodes.ServerUnreachable, activation.ErrorCode);
        Assert.Equal(LicenseErrorCodes.ServerUnreachable, renewal.ErrorCode);
    }

    [Fact]
    public async Task MissingBaseAddress_IsReportedAsUnreachable()
    {
        var client = new HttpLicenseClient(new HttpClient(new ThrowingHandler()));

        Assert.Equal(LicenseErrorCodes.ServerUnreachable, (await client.ActivateAsync(new ActivationRequest("k", Guid.NewGuid(), "p"))).ErrorCode);
    }

    [Fact]
    public async Task NonLicensingResponseBody_IsReportedAsRejected()
    {
        var client = new HttpLicenseClient(new HttpClient(new StaticHandler(HttpStatusCode.BadGateway, "<html>proxy</html>")) { BaseAddress = new Uri("https://x.test/") });

        var r = await client.ActivateAsync(new ActivationRequest("k", Guid.NewGuid(), "p"));

        Assert.False(r.IsSuccess);
        Assert.Equal(LicenseErrorCodes.ServerRejected, r.ErrorCode);
    }

    [Fact]
    public async Task UnreachableServer_DoesNotAffectOfflineEvaluation_OfAnExistingLease()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);
        var http = new HttpLicenseClient(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://down.test/") });
        var restarted = world.NewClientService(client: http);

        var evaluation = await restarted.InitializeAsync();
        var renewal = await restarted.RenewAsync();

        Assert.Equal(LicenseState.Active, evaluation.State);
        Assert.True(renewal.IsFailure);
        Assert.Equal(LicenseState.Active, restarted.State);   // still licensed
    }

    [Theory]
    [InlineData("https://licenses.example.com/", true)]
    [InlineData("http://localhost:5000/", true)]
    [InlineData("http://127.0.0.1:5000/", true)]
    [InlineData("http://licenses.example.com/", false)]
    [InlineData("ftp://licenses.example.com/", false)]
    [InlineData("", false)]
    [InlineData("not a url", false)]
    public void BaseAddress_RequiresHttps_ExceptLoopback(string url, bool accepted)
    {
        Assert.Equal(accepted, LicenseHttpExtensions.ValidateBaseAddress(url) is not null);
    }

    [Fact]
    public void AddLicenseHttpClient_RegistersTheHttpTransport()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licensing:ServerBaseUrl"] = "https://licenses.example.test/"
        }).Build();
        var services = new ServiceCollection();
        services.AddClientLicensing(config);
        services.AddLicenseHttpClient(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<HttpLicenseClient>(provider.GetRequiredService<ILicenseClient>());
    }
}

/// <summary>The real ASP.NET Core license server hosted in-process, driven through the real HTTP client. No external network.</summary>
public sealed class ApiIntegrationTests : IClassFixture<ApiIntegrationTests.ApiFactory>
{
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public static readonly DateTimeOffset ValidUntil = DateTimeOffset.UtcNow.AddYears(1);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LicenseServer:DevLicenses:0:ActivationKey"] = "API-KEY-1",
                ["LicenseServer:DevLicenses:0:CustomerId"] = "api-customer",
                ["LicenseServer:DevLicenses:0:ProductId"] = "genericpos",
                ["LicenseServer:DevLicenses:0:ValidFrom"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
                ["LicenseServer:DevLicenses:0:ValidUntil"] = ValidUntil.ToString("O"),
                ["LicenseServer:DevLicenses:0:Modules:0"] = "pos",
                ["LicenseServer:DevLicenses:0:Modules:1"] = "catalog",
                ["LicenseServer:DevLicenses:0:Features:0"] = "advancedreports",
                ["LicenseServer:DevLicenses:1:ActivationKey"] = "API-KEY-2",
                ["LicenseServer:DevLicenses:1:CustomerId"] = "api-customer",
                ["LicenseServer:DevLicenses:1:ProductId"] = "genericpos",
                ["LicenseServer:DevLicenses:1:ValidFrom"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
                ["LicenseServer:DevLicenses:1:ValidUntil"] = ValidUntil.ToString("O"),
                ["LicenseServer:DevLicenses:1:Modules:0"] = "pos",
                ["LicenseServer:DevLicenses:1:Modules:1"] = "catalog",
                ["LicenseServer:DevLicenses:1:Features:0"] = "advancedreports",
                ["LicenseServer:DevLicenses:2:ActivationKey"] = "API-KEY-3",
                ["LicenseServer:DevLicenses:2:CustomerId"] = "api-customer",
                ["LicenseServer:DevLicenses:2:ProductId"] = "genericpos",
                ["LicenseServer:DevLicenses:2:ValidFrom"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
                ["LicenseServer:DevLicenses:2:ValidUntil"] = ValidUntil.ToString("O"),
                ["LicenseServer:DevLicenses:2:Modules:0"] = "pos",
                ["LicenseServer:DevLicenses:2:Modules:1"] = "catalog",
                ["LicenseServer:DevLicenses:2:Features:0"] = "advancedreports"
            }));
        }
    }

    private readonly ApiFactory _factory;

    public ApiIntegrationTests(ApiFactory factory) => _factory = factory;

    private LicenseService NewClient(out EcdsaLicenseSigner signer, out InMemoryLicenseStore store, out InMemoryIdentityStore identity)
    {
        signer = (EcdsaLicenseSigner)_factory.Services.GetRequiredService<ILicenseSigner>();
        var verifier = new EcdsaLicenseVerifier([new TrustedLicenseKey(signer.KeyId, signer.ExportPublicKey())]);
        store = new InMemoryLicenseStore();
        identity = new InMemoryIdentityStore();
        return new LicenseService(
            new InstallationIdentityService(identity, TimeProvider.System),
            store,
            verifier,
            new HttpLicenseClient(_factory.CreateClient()),
            TimeProvider.System,
            new LicensingOptions("genericpos"),
            new LicensePolicy());
    }

    [Fact]
    public async Task Activate_OverHttp_ReturnsSignedLicense_ClientVerifiesAndStores()
    {
        var service = NewClient(out _, out var store, out _);

        var result = await service.ActivateAsync("API-KEY-1");

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        Assert.Equal(LicenseState.Active, result.Value.State);
        Assert.NotNull(store.Stored);
        Assert.True(service.IsModuleLicensed(new ModuleId("pos")));
        Assert.False(service.IsModuleLicensed(new ModuleId("accounting")));
    }

    [Fact]
    public async Task Activate_OverHttp_UnknownKey_ReturnsBusinessFailure()
    {
        var service = NewClient(out _, out var store, out _);

        var result = await service.ActivateAsync("NOT-A-KEY");

        Assert.True(result.IsFailure);
        Assert.Equal(LicenseErrorCodes.NotFound, result.Error.Code);
        Assert.Null(store.Stored);
    }

    [Fact]
    public async Task Renew_OverHttp_IssuesNewerVersion()
    {
        var service = NewClient(out _, out _, out _);
        var activated = await service.ActivateAsync("API-KEY-2");
        Assert.True(activated.IsSuccess);

        var renewed = await service.RenewAsync();

        Assert.True(renewed.IsSuccess);
        Assert.True(renewed.Value.Payload!.LicenseVersion > activated.Value.Payload!.LicenseVersion);
    }

    [Fact]
    public async Task Api_RejectsMalformedRequests_WithoutLeakingAServerError()
    {
        using var http = _factory.CreateClient();

        var response = await http.PostAsJsonAsync("api/licenses/activate",
            new ActivationRequest("", Guid.Empty, ""), LicenseSerializer.Options);
        var body = await response.Content.ReadFromJsonAsync<ActivationResponse>(LicenseSerializer.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(LicenseErrorCodes.InvalidRequest, body!.ErrorCode);
    }

    [Fact]
    public async Task Api_ResponsesNeverContainPrivateKeyMaterial()
    {
        using var http = _factory.CreateClient();
        var request = new ActivationRequest("API-KEY-3", Guid.NewGuid(), "genericpos");

        var response = await http.PostAsJsonAsync("api/licenses/activate", request, LicenseSerializer.Options);
        var text = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", text, StringComparison.OrdinalIgnoreCase);
    }
}
