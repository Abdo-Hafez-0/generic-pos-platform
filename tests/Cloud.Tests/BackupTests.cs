using System.Security.Cryptography;
using AdminPortal.Application;
using BackupServer.Application;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using Cloud.Infrastructure.Persistence;
using Licensing.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cloud.Tests;

/// <summary>A license with the cloud-backup module, activated, with a backup access token, ready to authenticate.</summary>
internal sealed class BackupCustomer
{
    public required CreateLicenseResponse License { get; init; }
    public required Guid Installation { get; init; }
    public required string Token { get; init; }
    public Guid LicenseId => License.License.LicenseId;
}

public sealed class BackupServiceTests : IDisposable
{
    private readonly CloudWorld _w = new();

    public void Dispose() => _w.Dispose();

    private async Task<BackupCustomer> CustomerAsync(string name = "Acme", bool withModule = true, bool activate = true, CloudWorld? world = null)
    {
        var w = world ?? _w;
        var license = await w.NewLicenseAsync(withModule ? ["cloud-backup"] : [], null, name);
        var installation = activate ? await w.ActivateAsync(license) : Guid.Empty;
        var token = ResultAssert.Ok(await w.Licenses.IssueBackupTokenAsync(w.Actor, license.License.LicenseId)).Token;
        return new BackupCustomer { License = license, Installation = installation, Token = token };
    }

    private async Task<BackupPrincipal> Login(BackupCustomer c, CloudWorld? world = null)
        => ResultAssert.Ok(await (world ?? _w).BackupAccess.AuthenticateAsync(c.Token));

    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private async Task<BackupRecord> Upload(BackupPrincipal who, byte[] data, string? sha = null, string? label = null, CloudWorld? world = null)
    {
        await using var stream = new MemoryStream(data);
        return ResultAssert.Ok(await (world ?? _w).Backups.UploadAsync(who, stream, new BackupUploadInfo(sha, label, "1.0.0")));
    }

    private string[] StagedFiles()
    {
        var staging = Path.Combine(_w.BackupDirectory, ".staging");
        return Directory.Exists(staging) ? Directory.GetFiles(staging) : [];
    }

    // ---- authentication ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("gpb_not-a-real-token")]
    public async Task Authenticate_MissingOrUnknownToken_IsUnauthorized(string? token)
        => ResultAssert.Fails(await _w.BackupAccess.AuthenticateAsync(token), CloudErrorCodes.Unauthorized);

    [Fact]
    public async Task Authenticate_ValidToken_IdentifiesTheLicense()
    {
        var c = await CustomerAsync();

        var p = await Login(c);

        Assert.Equal(c.LicenseId, p.LicenseId);
        Assert.Equal(c.Installation, p.InstallationId);
        Assert.Equal(c.License.License.CustomerId, p.CustomerId);
        Assert.True(p.CanUpload);
    }

    [Fact]
    public async Task Token_IsStoredOnlyAsAHash()
    {
        var c = await CustomerAsync();

        await using var db = _w.Get<IDbContextFactory<CloudDbContext>>().CreateDbContext();
        var stored = await db.Database.SqlQueryRaw<string>("SELECT TokenHash AS Value FROM bak_AccessTokens").ToListAsync();

        Assert.Equal([BackupTokens.Hash(c.Token)], stored);
        Assert.DoesNotContain(c.Token, stored[0]);
    }

    [Fact]
    public async Task RotatingTheToken_InvalidatesTheOldOne()
    {
        var c = await CustomerAsync();
        var rotated = ResultAssert.Ok(await _w.Licenses.IssueBackupTokenAsync(_w.Actor, c.LicenseId)).Token;

        ResultAssert.Fails(await _w.BackupAccess.AuthenticateAsync(c.Token), CloudErrorCodes.Unauthorized);
        Assert.True((await _w.BackupAccess.AuthenticateAsync(rotated)).IsSuccess);
    }

    [Fact]
    public async Task RevokingTheToken_StopsAccess()
    {
        var c = await CustomerAsync();
        await _w.Licenses.RevokeBackupTokenAsync(_w.Actor, c.LicenseId);

        ResultAssert.Fails(await _w.BackupAccess.AuthenticateAsync(c.Token), CloudErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task LicenseNotYetActivated_IsForbidden()
    {
        var c = await CustomerAsync(activate: false);

        ResultAssert.Fails(await _w.BackupAccess.AuthenticateAsync(c.Token), CloudErrorCodes.Forbidden);
    }

    [Fact]
    public async Task RevokedLicense_LosesAccessImmediately()
    {
        var c = await CustomerAsync();
        await _w.Licenses.RevokeAsync(_w.Actor, c.LicenseId, null);

        ResultAssert.Fails(await _w.BackupAccess.AuthenticateAsync(c.Token), CloudErrorCodes.Forbidden);
    }

    // ---- upload ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_StoresTheBytes_AndRecordsHashSizeAndOwner()
    {
        var c = await CustomerAsync();
        var p = await Login(c);
        var data = Bytes("a pretend encrypted sqlite backup");

        var record = await Upload(p, data, label: "  nightly  ");

        Assert.Equal(Sha(data), record.Sha256);
        Assert.Equal(data.Length, record.SizeBytes);
        Assert.Equal("nightly", record.Label);
        Assert.Equal("1.0.0", record.ClientVersion);
        Assert.Equal(c.LicenseId, record.LicenseId);
        Assert.Equal(c.Installation, record.InstallationId);
        Assert.Equal(CloudWorld.Start, record.CreatedAt);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_w.BackupDirectory, record.BackupId.ToString("N") + ".bak")));
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task Upload_WithMatchingClaimedHash_IsAccepted_CaseInsensitively()
    {
        var p = await Login(await CustomerAsync());
        var data = Bytes("payload");

        await Upload(p, data, sha: Sha(data).ToUpperInvariant());
    }

    [Fact]
    public async Task Upload_WithWrongClaimedHash_IsRejected_AndNothingIsKept()
    {
        var p = await Login(await CustomerAsync());
        await using var stream = new MemoryStream(Bytes("payload"));

        var r = await _w.Backups.UploadAsync(p, stream, new BackupUploadInfo(Sha(Bytes("other")), null, null));

        ResultAssert.Fails(r, CloudErrorCodes.BackupHashMismatch);
        Assert.Empty(StagedFiles());
        Assert.Empty(await _w.Backups.ListAsync(p));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task Upload_MalformedClaimedHash_IsRejected(string hash)
    {
        var p = await Login(await CustomerAsync());
        await using var stream = new MemoryStream(Bytes("payload"));

        ResultAssert.Fails(await _w.Backups.UploadAsync(p, stream, new BackupUploadInfo(hash, null, null)), CloudErrorCodes.Validation);
    }

    [Fact]
    public async Task Upload_EmptyBody_IsRejected()
    {
        var p = await Login(await CustomerAsync());
        await using var stream = new MemoryStream();

        ResultAssert.Fails(await _w.Backups.UploadAsync(p, stream, new BackupUploadInfo(null, null, null)), CloudErrorCodes.Validation);
        Assert.Empty(StagedFiles());
    }

    [Fact]
    public async Task Upload_LabelTooLong_IsRejected()
    {
        var p = await Login(await CustomerAsync());
        await using var stream = new MemoryStream(Bytes("x"));

        ResultAssert.Fails(await _w.Backups.UploadAsync(p, stream, new BackupUploadInfo(null, new string('l', 201), null)), CloudErrorCodes.Validation);
    }

    [Fact]
    public async Task Upload_OverTheSizeLimit_IsRejected_AndNothingIsKept()
    {
        using var small = new CloudWorld(new Dictionary<string, string?> { ["BackupServer:MaxBackupBytes"] = "10" });
        var c = await CustomerAsync(world: small);
        var p = await Login(c, small);
        await using var tooBig = new MemoryStream(new byte[11]);
        await using var exact = new MemoryStream(new byte[10]);

        ResultAssert.Fails(await small.Backups.UploadAsync(p, tooBig, new BackupUploadInfo(null, null, null)), CloudErrorCodes.TooLarge);
        Assert.True((await small.Backups.UploadAsync(p, exact, new BackupUploadInfo(null, null, null))).IsSuccess);

        var staging = Path.Combine(small.BackupDirectory, ".staging");
        Assert.Empty(Directory.GetFiles(staging));
        Assert.Single(await small.Backups.ListAsync(p));
    }

    // ---- who may upload ----------------------------------------------------------------------------------------

    [Fact]
    public async Task LicenseWithoutTheBackupModule_CannotUpload_ButCanStillList()
    {
        var p = await Login(await CustomerAsync(withModule: false));
        await using var stream = new MemoryStream(Bytes("x"));

        var r = await _w.Backups.UploadAsync(p, stream, new BackupUploadInfo(null, null, null));

        ResultAssert.Fails(r, CloudErrorCodes.Forbidden);
        Assert.Contains("cloud-backup", r.Error!.Message);
        Assert.Empty(await _w.Backups.ListAsync(p));
    }

    [Fact]
    public async Task SuspendedLicense_CannotUpload_ButCanReadAndRestoreItsBackups()
    {
        var c = await CustomerAsync();
        var record = await Upload(await Login(c), Bytes("precious"));
        await _w.Licenses.SuspendAsync(_w.Actor, c.LicenseId, null);

        var p = await Login(c);
        await using var stream = new MemoryStream(Bytes("more"));
        ResultAssert.Fails(await _w.Backups.UploadAsync(p, stream, new BackupUploadInfo(null, null, null)), CloudErrorCodes.Forbidden);

        Assert.Single(await _w.Backups.ListAsync(p));
        using var content = ResultAssert.Ok(await _w.Backups.OpenContentAsync(p, record.BackupId));
        Assert.Equal(Bytes("precious"), ReadAll(content));
    }

    [Fact]
    public async Task ExpiredLicense_CannotUpload_ButCanReadItsBackups_NothingIsDestroyed()
    {
        var c = await CustomerAsync();
        var record = await Upload(await Login(c), Bytes("precious"));
        _w.Clock.Advance(TimeSpan.FromDays(400));

        var p = await Login(c);
        await using var stream = new MemoryStream(Bytes("more"));
        var denied = await _w.Backups.UploadAsync(p, stream, new BackupUploadInfo(null, null, null));

        ResultAssert.Fails(denied, CloudErrorCodes.Forbidden);
        Assert.Contains("expired", denied.Error!.Message);
        using var content = ResultAssert.Ok(await _w.Backups.OpenContentAsync(p, record.BackupId));
        Assert.Equal(Bytes("precious"), ReadAll(content));
    }

    [Fact]
    public async Task EmptyRequiredModuleSetting_MeansNoEntitlementIsNeeded()
    {
        using var open = new CloudWorld(new Dictionary<string, string?> { ["BackupServer:RequiredModule"] = "" });
        var c = await CustomerAsync(withModule: false, world: open);

        await Upload(await Login(c, open), Bytes("x"), world: open);
    }

    [Fact]
    public async Task CustomRequiredModule_IsHonoured()
    {
        using var custom = new CloudWorld(new Dictionary<string, string?> { ["BackupServer:RequiredModule"] = "vault" });
        var license = await custom.NewLicenseAsync(["vault"]);
        await custom.ActivateAsync(license);
        var token = ResultAssert.Ok(await custom.Licenses.IssueBackupTokenAsync(custom.Actor, license.License.LicenseId)).Token;
        var p = ResultAssert.Ok(await custom.BackupAccess.AuthenticateAsync(token));

        await Upload(p, Bytes("x"), world: custom);
    }

    // ---- listing, download, delete, isolation ------------------------------------------------------------------

    [Fact]
    public async Task Download_ReturnsTheExactBytesUploaded()
    {
        var p = await Login(await CustomerAsync());
        var data = new byte[200_000];
        RandomNumberGenerator.Fill(data);
        var record = await Upload(p, data);

        using var content = ResultAssert.Ok(await _w.Backups.OpenContentAsync(p, record.BackupId));

        Assert.Equal(data, ReadAll(content));
        Assert.Equal(record.Sha256, Sha(data));
    }

    [Fact]
    public async Task List_IsNewestFirst()
    {
        var p = await Login(await CustomerAsync());
        var first = await Upload(p, Bytes("1"));
        _w.Clock.Advance(TimeSpan.FromHours(1));
        var second = await Upload(p, Bytes("2"));
        _w.Clock.Advance(TimeSpan.FromHours(1));
        var third = await Upload(p, Bytes("3"));

        Assert.Equal([third.BackupId, second.BackupId, first.BackupId], (await _w.Backups.ListAsync(p)).Select(b => b.BackupId));
    }

    [Fact]
    public async Task Retention_KeepsOnlyTheNewestBackups_AndDeletesTheirBytes()
    {
        using var w = new CloudWorld(new Dictionary<string, string?> { ["BackupServer:MaxBackupsPerLicense"] = "3" });
        var p = await Login(await CustomerAsync(world: w), w);
        var uploaded = new List<BackupRecord>();
        for (var i = 0; i < 5; i++)
        {
            w.Clock.Advance(TimeSpan.FromHours(1));
            uploaded.Add(await Upload(p, Bytes("backup " + i), world: w));
        }

        var kept = await w.Backups.ListAsync(p);

        Assert.Equal(uploaded.Skip(2).Reverse().Select(b => b.BackupId), kept.Select(b => b.BackupId));
        Assert.Equal(3, Directory.GetFiles(w.BackupDirectory, "*.bak").Length);
        foreach (var gone in uploaded.Take(2))
            Assert.False(File.Exists(Path.Combine(w.BackupDirectory, gone.BackupId.ToString("N") + ".bak")));
    }

    [Fact]
    public async Task Retention_IsPerLicense()
    {
        using var w = new CloudWorld(new Dictionary<string, string?> { ["BackupServer:MaxBackupsPerLicense"] = "1" });
        var a = await Login(await CustomerAsync("A", world: w), w);
        var b = await Login(await CustomerAsync("B", world: w), w);

        await Upload(a, Bytes("a1"), world: w);
        w.Clock.Advance(TimeSpan.FromMinutes(1));
        await Upload(b, Bytes("b1"), world: w);
        w.Clock.Advance(TimeSpan.FromMinutes(1));
        await Upload(a, Bytes("a2"), world: w);

        Assert.Single(await w.Backups.ListAsync(a));
        Assert.Single(await w.Backups.ListAsync(b));
    }

    [Fact]
    public async Task OneLicense_CanNeverSeeReadOrDeleteAnothersBackups()
    {
        var alice = await Login(await CustomerAsync("Alice"));
        var bob = await Login(await CustomerAsync("Bob"));
        var secret = await Upload(alice, Bytes("alice's data"));

        Assert.Empty(await _w.Backups.ListAsync(bob));
        ResultAssert.Fails(await _w.Backups.FindAsync(bob, secret.BackupId), CloudErrorCodes.NotFound);
        ResultAssert.Fails(await _w.Backups.OpenContentAsync(bob, secret.BackupId), CloudErrorCodes.NotFound);
        ResultAssert.Fails(await _w.Backups.DeleteAsync(bob, secret.BackupId), CloudErrorCodes.NotFound);

        Assert.Single(await _w.Backups.ListAsync(alice));
    }

    [Fact]
    public async Task Delete_RemovesTheRecordAndTheBytes()
    {
        var p = await Login(await CustomerAsync());
        var record = await Upload(p, Bytes("x"));

        Assert.True((await _w.Backups.DeleteAsync(p, record.BackupId)).IsSuccess);

        Assert.Empty(await _w.Backups.ListAsync(p));
        Assert.False(File.Exists(Path.Combine(_w.BackupDirectory, record.BackupId.ToString("N") + ".bak")));
        ResultAssert.Fails(await _w.Backups.DeleteAsync(p, record.BackupId), CloudErrorCodes.NotFound);
    }

    [Fact]
    public async Task ContentMissingFromDisk_IsReportedNotFound_NotACrash()
    {
        var p = await Login(await CustomerAsync());
        var record = await Upload(p, Bytes("x"));
        File.Delete(Path.Combine(_w.BackupDirectory, record.BackupId.ToString("N") + ".bak"));

        ResultAssert.Fails(await _w.Backups.OpenContentAsync(p, record.BackupId), CloudErrorCodes.NotFound);
    }

    [Fact]
    public async Task Backups_SurviveAServerRestart()
    {
        var c = await CustomerAsync();
        var record = await Upload(await Login(c), Bytes("durable"));

        using var restarted = new CloudWorld(new Dictionary<string, string?>(_w.Settings));
        var p = await Login(c, restarted);

        using var content = ResultAssert.Ok(await restarted.Backups.OpenContentAsync(p, record.BackupId));
        Assert.Equal(Bytes("durable"), ReadAll(content));
    }

    // ---- administration ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_SeesAllBackups_FiltersByLicense_AndDeletesWithAudit()
    {
        var a = await CustomerAsync("A");
        var b = await CustomerAsync("B");
        var one = await Upload(await Login(a), Bytes("11"));
        await Upload(await Login(b), Bytes("2222"));

        var all = await _w.Operations.ListBackupsAsync(null, null, null);
        var onlyA = await _w.Operations.ListBackupsAsync(a.LicenseId, null, null);

        Assert.Equal(2, all.Total);
        Assert.Equal([one.BackupId], onlyA.Items.Select(x => x.BackupId));

        Assert.True((await _w.Operations.DeleteBackupAsync(_w.Actor, one.BackupId)).IsSuccess);
        ResultAssert.Fails(await _w.Operations.DeleteBackupAsync(_w.Actor, one.BackupId), CloudErrorCodes.NotFound);
        Assert.False(File.Exists(Path.Combine(_w.BackupDirectory, one.BackupId.ToString("N") + ".bak")));

        var audit = await _w.Operations.QueryAuditAsync(new AuditFilter(Action: "backup.delete"), null, null);
        Assert.Equal(one.BackupId.ToString(), Assert.Single(audit.Items).EntityId);
    }

    [Fact]
    public async Task Dashboard_CountsEverything()
    {
        var a = await CustomerAsync("A");
        var b = await CustomerAsync("B");
        await Upload(await Login(a), Bytes("123"));
        await Upload(await Login(b), Bytes("12345"));
        await _w.Licenses.SuspendAsync(_w.Actor, b.LicenseId, null);
        var inactive = await _w.NewCustomerAsync("Dormant");
        await _w.Customers.DeactivateAsync(_w.Actor, inactive.Id);
        await _w.PublishOkAsync("catalog", "1.0.0");
        var withdrawn = await _w.PublishOkAsync("catalog", "1.1.0");
        await _w.Packages.WithdrawAsync(_w.Actor, withdrawn.PackageId, null);

        var d = await _w.Operations.GetDashboardAsync();

        Assert.Equal(new DashboardDto(
            Customers: 3, ActiveCustomers: 2,
            Licenses: 2, ActiveLicenses: 1, SuspendedLicenses: 1, RevokedLicenses: 0, Installations: 2,
            Modules: 2, PublishedPackages: 1, WithdrawnPackages: 1,
            Backups: 2, BackupBytes: 8), d);
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
