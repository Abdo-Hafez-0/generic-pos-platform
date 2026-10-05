extern alias LicenseApi;
extern alias UpdateApi;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using Cloud.Contracts.Backup;
using Licensing.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Security.Es256;
using Updates.Contracts;

namespace Cloud.Tests;

/// <summary>
/// The four server hosts (license, update, backup, administration) running in-process against ONE server database and the
/// same storage directories, driven only through their HTTP APIs - the way the deployed backend behaves.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private const string LicenseKeyId = "e2e-license-key";

    private readonly CloudWorld _world = new();
    private readonly ECDsa _licenseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Dictionary<string, string?> _settings;
    private readonly List<IDisposable> _disposables = [];
    private readonly HttpClient _admin;

    public EndToEndTests()
    {
        var pem = Path.Combine(_world.Dir, "license-signing-key.pem");
        File.WriteAllText(pem, _licenseKey.ExportPkcs8PrivateKeyPem());

        _settings = new Dictionary<string, string?>(_world.Settings)
        {
            ["LicenseServer:SigningKeyPemPath"] = pem,
            ["LicenseServer:KeyId"] = LicenseKeyId
        };

        _admin = Track(ApiHosting.Host<AdminPortalApiMarker>(_settings).CreateClient()).WithBearer(CloudWorld.AdminKey);
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_disposables)) d.Dispose();
        _licenseKey.Dispose();
        _world.Dispose();
    }

    private HttpClient Track(HttpClient client)
    {
        _disposables.Add(client);
        return client;
    }

    private HttpClient LicenseClient()
    {
        var host = ApiHosting.Host<LicenseApi::Program>(_settings);
        _disposables.Add(host);
        return Track(host.CreateClient());
    }

    private HttpClient UpdateClient()
    {
        var host = ApiHosting.Host<UpdateApi::Program>(_settings);
        _disposables.Add(host);
        return Track(host.CreateClient());
    }

    private HttpClient BackupClient(string? token)
    {
        var host = ApiHosting.Host<BackupServerApiMarker>(_settings);
        _disposables.Add(host);
        return Track(host.CreateClient()).WithBearer(token);
    }

    private Es256Verifier LicenseVerifier()
        => new([new TrustedPublicKey(LicenseKeyId, Convert.ToBase64String(_licenseKey.ExportSubjectPublicKeyInfo()))]);

    private async Task<(CreateLicenseResponse License, CustomerDto Customer)> VendorCreatesLicense(params string[] modules)
    {
        var customer = await (await _admin.PostAsJsonAsync("/api/admin/customers", new CustomerRequest("Acme Retail", "Pat", "pat@acme.test", null, null)))
            .Json<CustomerDto>(HttpStatusCode.Created);

        foreach (var module in modules)
            await _admin.PostAsJsonAsync("/api/admin/modules", new RegisterModuleRequest(module, module, null, null));

        var from = DateTimeOffset.UtcNow.AddMinutes(-1);
        var license = await (await _admin.PostAsJsonAsync("/api/admin/licenses",
            new CreateLicenseRequest(customer.Id.ToString(), "genericpos", from, from.AddYears(1), modules, ["advancedreports"])))
            .Json<CreateLicenseResponse>(HttpStatusCode.Created);

        return (license, customer);
    }

    private static async Task<LicensePayload> ParsePayload(SignedLicense license, Es256Verifier verifier)
    {
        var check = verifier.Verify(license.KeyId, license.Algorithm, Convert.FromBase64String(license.Payload), Convert.FromBase64String(license.Signature));
        Assert.Equal(SignatureCheck.Valid, check);
        await Task.CompletedTask;
        return LicenseSerializer.TryParsePayload(license.Payload)!;
    }

    // ---- licensing ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task VendorCreatesLicense_CustomerActivatesIt_WithAValidSignature_AndRenewalCarriesVendorChanges()
    {
        var (created, customer) = await VendorCreatesLicense("pos", "catalog");
        using var verifier = LicenseVerifier();
        var client = LicenseClient();
        var installation = Guid.NewGuid();

        var activation = await (await client.PostAsJsonAsync("/api/licenses/activate",
            new ActivationRequest(created.ActivationKey, installation, "genericpos"), LicenseSerializer.Options))
            .Content.ReadFromJsonAsync<ActivationResponse>(LicenseSerializer.Options);

        Assert.True(activation!.IsSuccess, activation.ErrorMessage);
        var payload = await ParsePayload(activation.License!, verifier);
        Assert.Equal(created.License.LicenseId, payload.LicenseId);
        Assert.Equal(customer.Id.ToString(), payload.CustomerId);
        Assert.Equal(installation, payload.InstallationId);
        Assert.Equal(["pos", "catalog"], payload.Modules);
        Assert.Equal(["advancedreports"], payload.Features);
        Assert.Equal(LicenseStatusClaim.Active, payload.Status);

        // The vendor sees the installation...
        var installations = await _admin.GetFromJsonAsync<PagedResult<InstallationDto>>("/api/admin/installations");
        Assert.Equal(installation, Assert.Single(installations!.Items).InstallationId);

        // ...suspends the license, adds a feature, and the customer learns of it at the next renewal.
        await _admin.PostAsJsonAsync($"/api/admin/licenses/{payload.LicenseId}/suspend", new StatusChangeRequest("late payment"));
        await _admin.PutAsJsonAsync($"/api/admin/licenses/{payload.LicenseId}/entitlements", new SetEntitlementsRequest(["pos"], ["advancedreports", "labels"]));

        var renewal = await (await client.PostAsJsonAsync("/api/licenses/renew",
            new RenewalRequest(payload.LicenseId, installation, payload.LicenseVersion), LicenseSerializer.Options))
            .Content.ReadFromJsonAsync<RenewalResponse>(LicenseSerializer.Options);

        var renewed = await ParsePayload(renewal!.License!, verifier);
        Assert.Equal(LicenseStatusClaim.Suspended, renewed.Status);
        Assert.Equal(["pos"], renewed.Modules);
        Assert.Equal(["advancedreports", "labels"], renewed.Features);
        Assert.True(renewed.LicenseVersion > payload.LicenseVersion);
    }

    [Fact]
    public async Task ARevokedLicense_CannotBeActivated_AndAnUnknownKeyIsRefused()
    {
        var (created, _) = await VendorCreatesLicense();
        await _admin.PostAsync($"/api/admin/licenses/{created.License.LicenseId}/revoke", null);
        var client = LicenseClient();

        var revoked = await (await client.PostAsJsonAsync("/api/licenses/activate",
            new ActivationRequest(created.ActivationKey, Guid.NewGuid(), "genericpos"), LicenseSerializer.Options))
            .Content.ReadFromJsonAsync<ActivationResponse>(LicenseSerializer.Options);
        var unknown = await (await client.PostAsJsonAsync("/api/licenses/activate",
            new ActivationRequest("AAAAA-AAAAA-AAAAA-AAAAA-AAAAA", Guid.NewGuid(), "genericpos"), LicenseSerializer.Options))
            .Content.ReadFromJsonAsync<ActivationResponse>(LicenseSerializer.Options);

        Assert.Equal(LicenseErrorCodes.Revoked, revoked!.ErrorCode);
        Assert.Equal(LicenseErrorCodes.NotFound, unknown!.ErrorCode);
    }

    [Fact]
    public async Task ALicenseServerRestart_LosesNothing()
    {
        var (created, _) = await VendorCreatesLicense();
        var installation = Guid.NewGuid();

        var first = LicenseClient();
        var activated = await (await first.PostAsJsonAsync("/api/licenses/activate",
            new ActivationRequest(created.ActivationKey, installation, "genericpos"), LicenseSerializer.Options))
            .Content.ReadFromJsonAsync<ActivationResponse>(LicenseSerializer.Options);
        Assert.True(activated!.IsSuccess);

        var restarted = LicenseClient(); // a brand-new host over the same server database
        var renewal = await (await restarted.PostAsJsonAsync("/api/licenses/renew",
            new RenewalRequest(created.License.LicenseId, installation, 1), LicenseSerializer.Options))
            .Content.ReadFromJsonAsync<RenewalResponse>(LicenseSerializer.Options);

        Assert.True(renewal!.IsSuccess, renewal.ErrorMessage);
    }

    [Fact]
    public void TheLicenseServer_RefusesToRunOutsideDevelopment_WithoutADurableStore()
    {
        var withoutDatabase = new Dictionary<string, string?>(_settings) { ["CloudDatabase:ConnectionString"] = "" };
        using var host = ApiHosting.Host<LicenseApi::Program>(withoutDatabase, "Production");

        var ex = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("CloudDatabase:ConnectionString", ex.ToString());
    }

    // ---- backups -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task VendorEntitlesBackup_CustomerBacksUp_VendorSeesAndRemovesIt()
    {
        var (created, _) = await VendorCreatesLicense("cloud-backup");
        var id = created.License.LicenseId;

        var activated = await (await LicenseClient().PostAsJsonAsync("/api/licenses/activate",
            new ActivationRequest(created.ActivationKey, Guid.NewGuid(), "genericpos"), LicenseSerializer.Options))
            .Content.ReadFromJsonAsync<ActivationResponse>(LicenseSerializer.Options);
        Assert.True(activated!.IsSuccess);

        var token = (await (await _admin.PostAsync($"/api/admin/licenses/{id}/backup-token", null)).Json<BackupTokenResponse>(HttpStatusCode.OK)).Token;
        var backups = BackupClient(token);
        var data = new byte[5_000];
        RandomNumberGenerator.Fill(data);

        var stored = await (await backups.PostAsync("/api/backups?label=e2e", new ByteArrayContent(data))).Json<BackupDto>(HttpStatusCode.Created);

        var vendorView = await _admin.GetFromJsonAsync<PagedResult<BackupDto>>($"/api/admin/backups?licenseId={id}");
        Assert.Equal(stored.BackupId, Assert.Single(vendorView!.Items).BackupId);
        Assert.Equal(5_000, (await _admin.GetFromJsonAsync<DashboardDto>("/api/admin/dashboard"))!.BackupBytes);

        Assert.Equal(data, await (await backups.GetAsync($"/api/backups/{stored.BackupId}/content")).Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/admin/backups/{stored.BackupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await backups.GetAsync($"/api/backups/{stored.BackupId}")).StatusCode);
    }

    [Fact]
    public async Task RevokingTheBackupToken_EndsAccessImmediately()
    {
        var (created, _) = await VendorCreatesLicense("cloud-backup");
        var id = created.License.LicenseId;
        await LicenseClient().PostAsJsonAsync("/api/licenses/activate",
            new ActivationRequest(created.ActivationKey, Guid.NewGuid(), "genericpos"), LicenseSerializer.Options);
        var token = (await (await _admin.PostAsync($"/api/admin/licenses/{id}/backup-token", null)).Json<BackupTokenResponse>(HttpStatusCode.OK)).Token;
        var backups = BackupClient(token);
        Assert.Equal(HttpStatusCode.OK, (await backups.GetAsync("/api/backups")).StatusCode);

        await _admin.DeleteAsync($"/api/admin/licenses/{id}/backup-token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await backups.GetAsync("/api/backups")).StatusCode);
    }

    // ---- updates -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task VendorPublishesAPackage_TheUpdateServerOffersIt_WithdrawingHidesIt()
    {
        await _admin.PostAsJsonAsync("/api/admin/modules", new RegisterModuleRequest("catalog", "Catalog", null, null));
        var path = _world.MakePackage("catalog", "1.1.0");
        var bytes = await File.ReadAllBytesAsync(path);

        var published = await (await _admin.PostAsync("/api/admin/packages", new ByteArrayContent(bytes))).Json<PackageDto>(HttpStatusCode.Created);
        var updates = UpdateClient();
        var request = new UpdateCheckRequest("1.0.0", "net10.0", [new InstalledTarget("catalog", "1.0.0")]);

        async Task<IReadOnlyList<UpdateInfo>> Check()
            => (await (await updates.PostAsJsonAsync("/api/updates/check", request, PackageManifestSerializer.Options))
                .Content.ReadFromJsonAsync<UpdateCheckResponse>(PackageManifestSerializer.Options))!.Updates;

        var offered = Assert.Single(await Check());
        Assert.Equal(published.PackageId, offered.PackageId);
        Assert.Equal("1.1.0", offered.Version);
        Assert.Equal(Sha256Hex.Compute(bytes), offered.DownloadSha256);

        var download = await updates.GetAsync($"/api/updates/packages/{published.PackageId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());

        await _admin.PostAsJsonAsync($"/api/admin/packages/{published.PackageId}/withdraw", new StatusChangeRequest("regression"));
        Assert.Empty(await Check());
        Assert.Equal(HttpStatusCode.NotFound, (await updates.GetAsync($"/api/updates/packages/{published.PackageId}")).StatusCode);

        await _admin.PostAsync($"/api/admin/packages/{published.PackageId}/restore", null);
        Assert.Single(await Check());
        Assert.Equal(HttpStatusCode.OK, (await updates.GetAsync($"/api/updates/packages/{published.PackageId}")).StatusCode);
    }

    [Fact]
    public async Task APackagePublishedBeforeAnUpdateServerRestart_IsStillOffered()
    {
        await _admin.PostAsJsonAsync("/api/admin/modules", new RegisterModuleRequest("catalog", "Catalog", null, null));
        await _admin.PostAsync("/api/admin/packages", new ByteArrayContent(await File.ReadAllBytesAsync(_world.MakePackage("catalog", "1.1.0"))));

        var restarted = UpdateClient();
        var response = await (await restarted.PostAsJsonAsync("/api/updates/check",
            new UpdateCheckRequest("1.0.0", "net10.0", [new InstalledTarget("catalog", "1.0.0")]), PackageManifestSerializer.Options))
            .Content.ReadFromJsonAsync<UpdateCheckResponse>(PackageManifestSerializer.Options);

        Assert.Single(response!.Updates);
    }
}
