using System.Security.Cryptography;
using Client.CloudBackup.Http;
using CloudBackup.Contracts;
using Cloud.Contracts.Admin;
using Microsoft.AspNetCore.Mvc.Testing;
using Security.BackupEnvelope;

namespace Cloud.Tests;

/// <summary>
/// MISS-04e2: the desktop's cloud backup transport (Client.CloudBackup.Http) against the real BackupServer.Api (Stage 9) with real
/// licenses and backup access keys: an encrypted backup goes up, is listed, comes back byte for byte and still opens with the shop key;
/// refusals and outages become plain results.
/// </summary>
public sealed class CloudBackupTransportTests : IDisposable
{
    private readonly CloudWorld _world = new();
    private readonly WebApplicationFactory<BackupServerApiMarker> _host;

    public CloudBackupTransportTests() => _host = ApiHosting.Host<BackupServerApiMarker>(_world.Settings, clock: _world.Clock);

    public void Dispose()
    {
        _host.Dispose();
        _world.Dispose();
    }

    private async Task<string> AccessKeyAsync(bool withModule = true)
    {
        var license = await _world.NewLicenseAsync(withModule ? ["cloud-backup"] : [], null, "Acme " + Guid.NewGuid().ToString("N")[..6]);
        await _world.ActivateAsync(license);
        return ResultAssert.Ok(await _world.Licenses.IssueBackupTokenAsync(_world.Actor, license.License.LicenseId)).Token;
    }

    private HttpBackupClient Client() => new(_host.CreateClient());

    private static async Task<(byte[] File, BackupKey Key, byte[] Database)> EncryptedBackupAsync()
    {
        var database = RandomNumberGenerator.GetBytes(200_000);
        using var vendor = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var key = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);
        using var output = new MemoryStream();
        await BackupEnvelope.WriteAsync(new MemoryStream(database), output, key,
            new EscrowPublicKey("escrow-test", Convert.ToBase64String(vendor.ExportSubjectPublicKeyInfo())),
            new EnvelopeMetadata("1.2.3", ["m1"], database.Length, Convert.ToHexStringLower(SHA256.HashData(database))), DateTimeOffset.UtcNow);
        return (output.ToArray(), key, database);
    }

    [Fact]
    public async Task An_encrypted_backup_goes_up_is_listed_comes_back_unchanged_and_still_opens()
    {
        var access = await AccessKeyAsync();
        var (file, key, database) = await EncryptedBackupAsync();
        var sha = Convert.ToHexStringLower(SHA256.HashData(file));

        var uploaded = await Client().UploadAsync(access, new MemoryStream(file), sha, "genericpos-20261010-230000.db", "1.2.3");

        Assert.True(uploaded.IsSuccess, uploaded.IsFailure ? uploaded.Error.Description : null);
        Assert.Equal(file.Length, uploaded.Value.SizeBytes);
        Assert.Equal("genericpos-20261010-230000.db", uploaded.Value.Label);

        var listed = Assert.Single((await Client().ListAsync(access)).Value);
        Assert.Equal(uploaded.Value.Id, listed.Id);
        Assert.Equal(sha, listed.Sha256);

        var downloaded = await Client().DownloadAsync(access, listed.Id);
        Assert.True(downloaded.IsSuccess);
        using var copy = new MemoryStream();
        await using (var stream = downloaded.Value) await stream.CopyToAsync(copy);
        Assert.Equal(file, copy.ToArray());

        using var opened = new MemoryStream();
        var header = BackupEnvelope.ReadHeader(new MemoryStream(copy.ToArray()));
        await BackupEnvelope.DecryptAsync(new MemoryStream(copy.ToArray()), opened, BackupEnvelope.UnwrapDataKey(header, key));
        Assert.Equal(database, opened.ToArray());

        Assert.True((await Client().DeleteAsync(access, listed.Id)).IsSuccess);
        Assert.Empty((await Client().ListAsync(access)).Value);
        Assert.Equal(BackupClientErrorCodes.NotFound, (await Client().DownloadAsync(access, listed.Id)).Error.Code);
    }

    [Fact]
    public async Task A_wrong_access_key_or_a_license_without_cloud_backup_is_refused_in_plain_words()
    {
        var (file, _, _) = await EncryptedBackupAsync();
        var sha = Convert.ToHexStringLower(SHA256.HashData(file));

        var unknown = await Client().UploadAsync("gpb_not-a-real-access-key-000000", new MemoryStream(file), sha, "x.db", "1.2.3");
        Assert.Equal(BackupClientErrorCodes.Refused, unknown.Error.Code);
        Assert.Contains("ask the vendor whether cloud backup is included", unknown.Error.Description);

        var notEntitled = await Client().UploadAsync(await AccessKeyAsync(withModule: false), new MemoryStream(file), sha, "x.db", "1.2.3");
        Assert.Equal(BackupClientErrorCodes.Refused, notEntitled.Error.Code);
        Assert.Equal(BackupClientErrorCodes.Refused, (await Client().ListAsync("gpb_not-a-real-access-key-000000")).Error.Code);
    }

    [Fact]
    public async Task A_damaged_upload_is_refused_by_its_fingerprint()
    {
        var access = await AccessKeyAsync();
        var (file, _, _) = await EncryptedBackupAsync();

        var refused = await Client().UploadAsync(access, new MemoryStream(file), new string('0', 64), "x.db", "1.2.3");

        Assert.True(refused.IsFailure);
        Assert.Empty((await Client().ListAsync(access)).Value);
    }

    [Fact]
    public async Task No_server_configured_or_no_server_answering_means_try_later_never_an_exception()
    {
        var notConfigured = await new HttpBackupClient(new HttpClient()).ListAsync("x");
        Assert.Equal(BackupClientErrorCodes.Unavailable, notConfigured.Error.Code);

        using var closed = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/"), Timeout = TimeSpan.FromSeconds(5) };
        var down = await new HttpBackupClient(closed).UploadAsync("x", new MemoryStream([1, 2, 3]), "", "x.db", "1.2.3");
        Assert.Equal(BackupClientErrorCodes.Unavailable, down.Error.Code);
        Assert.Contains("local backups are not affected", down.Error.Description);
        Assert.Equal(BackupClientErrorCodes.Unavailable, (await new HttpBackupClient(closed).DownloadAsync("x", Guid.NewGuid())).Error.Code);
    }

    [Fact]
    public void Only_https_or_loopback_http_is_accepted_as_the_server_address()
    {
        Assert.NotNull(CloudBackupHttpExtensions.ValidateBaseAddress("https://backup.example.com/"));
        Assert.NotNull(CloudBackupHttpExtensions.ValidateBaseAddress("http://127.0.0.1:5300/"));
        Assert.Null(CloudBackupHttpExtensions.ValidateBaseAddress("http://backup.example.com/"));
        Assert.Null(CloudBackupHttpExtensions.ValidateBaseAddress("not a url"));
        Assert.Null(CloudBackupHttpExtensions.ValidateBaseAddress(null));
    }
}
