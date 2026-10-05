using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using Cloud.Contracts.Backup;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cloud.Tests;

public sealed class BackupApiTests : IDisposable
{
    private readonly CloudWorld _world;
    private readonly WebApplicationFactory<BackupServerApiMarker> _host;

    public BackupApiTests()
    {
        _world = new CloudWorld();
        _host = ApiHosting.Host<BackupServerApiMarker>(_world.Settings, clock: _world.Clock);
    }

    public void Dispose()
    {
        _host.Dispose();
        _world.Dispose();
    }

    private async Task<(CreateLicenseResponse License, string Token)> Customer(string name = "Acme", bool withModule = true)
    {
        var license = await _world.NewLicenseAsync(withModule ? ["cloud-backup"] : [], null, name);
        await _world.ActivateAsync(license);
        var token = ResultAssert.Ok(await _world.Licenses.IssueBackupTokenAsync(_world.Actor, license.License.LicenseId)).Token;
        return (license, token);
    }

    private HttpClient Client(string? token) => _host.CreateClient().WithBearer(token);

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static ByteArrayContent Body(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new("application/octet-stream");
        return content;
    }

    // ---- authentication ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_NeedsNoToken()
    {
        using var client = Client(null);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("gpb_not-a-token")]
    public async Task EveryBackupEndpoint_RejectsMissingOrUnknownTokens(string? token)
    {
        using var client = Client(token);
        var id = Guid.NewGuid();

        foreach (var response in new[]
        {
            await client.GetAsync("/api/backups"),
            await client.PostAsync("/api/backups", Body([1])),
            await client.GetAsync($"/api/backups/{id}"),
            await client.GetAsync($"/api/backups/{id}/content"),
            await client.DeleteAsync($"/api/backups/{id}")
        })
        {
            Assert.Equal(CloudErrorCodes.Unauthorized, (await response.Error(HttpStatusCode.Unauthorized)).Code);
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        }
    }

    [Fact]
    public async Task TheAdminKey_IsNotABackupToken()
    {
        using var client = Client(CloudWorld.AdminKey);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/backups")).StatusCode);
    }

    [Fact]
    public async Task RevokedLicense_IsForbidden()
    {
        var (license, token) = await Customer();
        await _world.Licenses.RevokeAsync(_world.Actor, license.License.LicenseId, null);
        using var client = Client(token);

        Assert.Equal(CloudErrorCodes.Forbidden, (await (await client.GetAsync("/api/backups")).Error(HttpStatusCode.Forbidden)).Code);
    }

    // ---- the round trip ----------------------------------------------------------------------------------------

    [Fact]
    public async Task UploadListDownloadDelete_RoundTrip()
    {
        var (license, token) = await Customer();
        using var client = Client(token);
        var data = new byte[300_000];
        RandomNumberGenerator.Fill(data);

        using var upload = Body(data);
        upload.Headers.Add(BackupHeaders.Sha256, Sha(data));
        upload.Headers.Add(BackupHeaders.ClientVersion, "2.1.0");
        var created = await client.PostAsync("/api/backups?label=nightly", upload);
        var dto = await created.Json<BackupDto>(HttpStatusCode.Created);

        Assert.Equal(license.License.LicenseId, dto.LicenseId);
        Assert.Equal(data.Length, dto.SizeBytes);
        Assert.Equal(Sha(data), dto.Sha256);
        Assert.Equal("nightly", dto.Label);
        Assert.Equal("2.1.0", dto.ClientVersion);
        Assert.Equal($"/api/backups/{dto.BackupId}", created.Headers.Location!.OriginalString);

        Assert.Equal(dto.BackupId, Assert.Single((await client.GetFromJsonAsync<List<BackupDto>>("/api/backups"))!).BackupId);
        Assert.Equal(dto.BackupId, (await client.GetFromJsonAsync<BackupDto>($"/api/backups/{dto.BackupId}"))!.BackupId);

        var download = await client.GetAsync($"/api/backups/{dto.BackupId}/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(data, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal(Sha(data), download.Headers.GetValues(BackupHeaders.Sha256).Single());
        Assert.True(download.Headers.CacheControl!.NoStore);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/backups/{dto.BackupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/backups/{dto.BackupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/backups/{dto.BackupId}/content")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<BackupDto>>("/api/backups"))!);
    }

    [Fact]
    public async Task Upload_WithWrongHash_IsABadRequest_AndStoresNothing()
    {
        var (_, token) = await Customer();
        using var client = Client(token);
        using var upload = Body([1, 2, 3]);
        upload.Headers.Add(BackupHeaders.Sha256, Sha([9, 9, 9]));

        var response = await client.PostAsync("/api/backups", upload);

        Assert.Equal(CloudErrorCodes.BackupHashMismatch, (await response.Error(HttpStatusCode.BadRequest)).Code);
        Assert.Empty((await client.GetFromJsonAsync<List<BackupDto>>("/api/backups"))!);
    }

    [Fact]
    public async Task Upload_EmptyBody_IsABadRequest()
    {
        var (_, token) = await Customer();
        using var client = Client(token);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/backups", Body([]))).StatusCode);
    }

    [Fact]
    public async Task Upload_OverTheLimit_Is413()
    {
        using var small = new CloudWorld(new Dictionary<string, string?> { ["BackupServer:MaxBackupBytes"] = "16" });
        using var host = ApiHosting.Host<BackupServerApiMarker>(small.Settings);
        var license = await small.NewLicenseAsync(["cloud-backup"]);
        await small.ActivateAsync(license);
        var token = ResultAssert.Ok(await small.Licenses.IssueBackupTokenAsync(small.Actor, license.License.LicenseId)).Token;
        using var client = host.CreateClient().WithBearer(token);

        var response = await client.PostAsync("/api/backups", Body(new byte[17]));

        Assert.Equal(CloudErrorCodes.TooLarge, (await response.Error(HttpStatusCode.RequestEntityTooLarge)).Code);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/backups", Body(new byte[16]))).StatusCode);
    }

    [Fact]
    public async Task LicenseWithoutTheModule_IsForbiddenToUpload_ButNotToList()
    {
        var (_, token) = await Customer(withModule: false);
        using var client = Client(token);

        var upload = await client.PostAsync("/api/backups", Body([1]));

        Assert.Equal(CloudErrorCodes.Forbidden, (await upload.Error(HttpStatusCode.Forbidden)).Code);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/backups")).StatusCode);
    }

    [Fact]
    public async Task CustomersCannotReachEachOthersBackups()
    {
        var (_, aliceToken) = await Customer("Alice");
        var (_, bobToken) = await Customer("Bob");
        using var alice = Client(aliceToken);
        using var bob = Client(bobToken);
        var dto = await (await alice.PostAsync("/api/backups", Body([4, 2]))).Json<BackupDto>(HttpStatusCode.Created);

        Assert.Empty((await bob.GetFromJsonAsync<List<BackupDto>>("/api/backups"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/backups/{dto.BackupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/backups/{dto.BackupId}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/backups/{dto.BackupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/backups/{dto.BackupId}/content")).StatusCode);
    }

    [Fact]
    public async Task ExpiredLicense_CanStillDownload_ButNotUpload()
    {
        var (_, token) = await Customer();
        using var client = Client(token);
        var dto = await (await client.PostAsync("/api/backups", Body([7]))).Json<BackupDto>(HttpStatusCode.Created);

        _world.Clock.Advance(TimeSpan.FromDays(400));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/backups", Body([8]))).StatusCode);
        Assert.Equal(new byte[] { 7 }, await (await client.GetAsync($"/api/backups/{dto.BackupId}/content")).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ConfigurationMissing_FailsStartup()
    {
        var settings = new Dictionary<string, string?>(_world.Settings) { ["BackupServer:StorageDirectory"] = "" };
        using var host = ApiHosting.Host<BackupServerApiMarker>(settings);

        var ex = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("BackupServer:StorageDirectory", ex.ToString());
    }
}
