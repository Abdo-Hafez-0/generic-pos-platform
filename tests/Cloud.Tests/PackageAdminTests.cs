using AdminPortal.Application;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using Security.Es256;
using Security.Es256.Signing;
using UpdateServer.Application;
using Updates.Contracts;
using Updates.Package;

namespace Cloud.Tests;

public sealed class PackagePublishingTests : IDisposable
{
    private readonly CloudWorld _w = new();

    public void Dispose() => _w.Dispose();

    private static string[] StagedFiles(CloudWorld w)
    {
        var staging = Path.Combine(w.PackageDirectory, ".staging");
        return Directory.Exists(staging) ? Directory.GetFiles(staging) : [];
    }

    [Fact]
    public async Task Publish_ValidModulePackage_IsCatalogued_AndItsBytesStored()
    {
        await _w.NewModuleAsync("catalog");
        var path = _w.MakePackage("catalog", "1.2.0");

        var dto = ResultAssert.Ok(await _w.PublishAsync(path, "  fixes a bug  "));

        Assert.Equal("Module", dto.PackageType);
        Assert.Equal("catalog", dto.TargetId);
        Assert.Equal("1.2.0", dto.Version);
        Assert.Equal("Published", dto.Status);
        Assert.Equal("fixes a bug", dto.ReleaseNotes);
        Assert.Equal("tester", dto.PublishedBy);
        Assert.Equal(CloudWorld.PublisherKeyId, dto.KeyId);
        Assert.Equal(CloudWorld.Start, dto.PublishedAt);
        Assert.Null(dto.WithdrawnAt);
        Assert.Equal(new FileInfo(path).Length, dto.SizeBytes);
        Assert.Equal(Sha256Hex.Compute(File.ReadAllBytes(path)), dto.Sha256);

        var stored = Path.Combine(_w.PackageDirectory, dto.PackageId.ToString("N") + PackageFormat.Extension);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(stored));
        Assert.Empty(StagedFiles(_w));
    }

    [Fact]
    public async Task Publish_CorePackage_NeedsNoRegistryEntry()
    {
        var dto = ResultAssert.Ok(await _w.PublishAsync(_w.MakePackage("core", "2.0.0", PackageType.Core)));

        Assert.Equal("Core", dto.PackageType);
        Assert.Equal("core", dto.TargetId);
    }

    [Fact]
    public async Task Publish_ForAnUnregisteredModule_IsRejected_AndLeavesNothingBehind()
    {
        var r = await _w.PublishAsync(_w.MakePackage("ghost", "1.0.0"));

        ResultAssert.Fails(r, CloudErrorCodes.Validation);
        Assert.Contains("registry", r.Error!.Message);
        Assert.Empty(await _w.Packages.ListAsync(null, null));
        Assert.Empty(StagedFiles(_w));
        Assert.Empty(Directory.GetFiles(_w.PackageDirectory, "*" + PackageFormat.Extension));
    }

    [Fact]
    public async Task Publish_ForARetiredModule_IsInvalidState()
    {
        await _w.NewModuleAsync("old");
        await _w.Modules.RetireAsync(_w.Actor, "old");

        ResultAssert.Fails(await _w.PublishAsync(_w.MakePackage("old", "1.0.0")), CloudErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Publish_ModuleIdMatching_IsExact()
    {
        await _w.NewModuleAsync("catalog");

        ResultAssert.Fails(await _w.PublishAsync(_w.MakePackage("catalog-extra", "1.0.0")), CloudErrorCodes.Validation);
    }

    [Fact]
    public async Task Publish_GarbageBytes_AreRejected()
    {
        await using var stream = new MemoryStream("this is not a package"u8.ToArray());

        var r = await _w.Packages.PublishAsync(_w.Actor, stream, null);

        ResultAssert.Fails(r, CloudErrorCodes.InvalidPackage);
        Assert.Empty(StagedFiles(_w));
    }

    [Fact]
    public async Task Publish_EmptyBody_IsRejected()
    {
        await using var stream = new MemoryStream();

        ResultAssert.Fails(await _w.Packages.PublishAsync(_w.Actor, stream, null), CloudErrorCodes.InvalidPackage);
    }

    [Fact]
    public async Task Publish_PayloadThatDoesNotMatchTheManifest_IsRejected()
    {
        await _w.NewModuleAsync("catalog");
        var good = _w.MakePackage("catalog", "1.0.0");
        var sameLength = Rewrite(good, payload: [("module.dll", "binary catalog 9.9.9")]);
        var otherLength = Rewrite(good, payload: [("module.dll", "different bytes")]);

        var hash = await _w.PublishAsync(sameLength);
        var length = await _w.PublishAsync(otherLength);

        ResultAssert.Fails(hash, CloudErrorCodes.InvalidPackage);
        Assert.Contains("manifest hash", hash.Error!.Message);
        ResultAssert.Fails(length, CloudErrorCodes.InvalidPackage);
        Assert.Contains("unexpected length", length.Error!.Message);
    }

    [Fact]
    public async Task Publish_PackageWithAnUnlistedFile_IsRejected()
    {
        await _w.NewModuleAsync("catalog");
        var good = _w.MakePackage("catalog", "1.0.0");
        var extra = Rewrite(good, payload: [("module.dll", "binary catalog 1.0.0"), ("hidden.dll", "evil")]);

        var r = await _w.PublishAsync(extra);

        ResultAssert.Fails(r, CloudErrorCodes.InvalidPackage);
        Assert.Contains("unlisted", r.Error!.Message);
    }

    [Fact]
    public async Task Publish_PackageMissingAListedFile_IsRejected()
    {
        await _w.NewModuleAsync("catalog");
        var good = _w.MakePackage("catalog", "1.0.0");
        var missing = Rewrite(good, payload: [("other.dll", "x")], expectListedPayload: false);

        ResultAssert.Fails(await _w.PublishAsync(missing), CloudErrorCodes.InvalidPackage);
    }

    [Fact]
    public async Task Publish_DuplicatePackageId_IsConflict()
    {
        await _w.NewModuleAsync("catalog");
        var id = Guid.NewGuid();
        ResultAssert.Ok(await _w.PublishAsync(_w.MakePackage("catalog", "1.0.0", packageId: id)));

        ResultAssert.Fails(await _w.PublishAsync(_w.MakePackage("catalog", "1.1.0", packageId: id)), CloudErrorCodes.Conflict);
    }

    [Fact]
    public async Task Publish_SameTargetAndVersion_IsConflict_EvenWhenWithdrawn()
    {
        var first = await _w.PublishOkAsync("catalog", "1.0.0");

        ResultAssert.Fails(await _w.PublishAsync(_w.MakePackage("catalog", "1.0.0")), CloudErrorCodes.Conflict);

        await _w.Packages.WithdrawAsync(_w.Actor, first.PackageId, null);
        ResultAssert.Fails(await _w.PublishAsync(_w.MakePackage("catalog", "1.0.0")), CloudErrorCodes.Conflict);
        Assert.Empty(StagedFiles(_w));
    }

    [Fact]
    public async Task Publish_ReleaseNotesTooLong_IsRejected()
    {
        await _w.NewModuleAsync("catalog");

        var r = await _w.PublishAsync(_w.MakePackage("catalog", "1.0.0"), new string('n', PackageAdminService.MaxReleaseNotesLength + 1));

        ResultAssert.Fails(r, CloudErrorCodes.Validation);
        Assert.Empty(StagedFiles(_w));
    }

    [Fact]
    public async Task Publish_OverTheSizeLimit_IsRejected_AndNothingIsKept()
    {
        using var small = new CloudWorld(new Dictionary<string, string?> { ["UpdateServer:MaxPackageBytes"] = "200" });
        await small.NewModuleAsync("catalog");

        var r = await small.PublishAsync(small.MakePackage("catalog", "1.0.0"));

        ResultAssert.Fails(r, CloudErrorCodes.TooLarge);
        Assert.Empty(StagedFiles(small));
        Assert.Empty(await small.Packages.ListAsync(null, null));
    }

    // ---- server-side signature policy ---------------------------------------------------------------------------

    [Fact]
    public async Task Signature_WithTrustedKeysConfigured_AcceptsTheTrustedPublisher_AndRejectsOthers()
    {
        using var w = new CloudWorld(trustPublisher: true);
        await w.NewModuleAsync("catalog");
        using var stranger = Es256Signer.GenerateEphemeral("stranger");

        ResultAssert.Ok(await w.PublishAsync(w.MakePackage("catalog", "1.0.0")));
        var rejected = await w.PublishAsync(w.MakePackage("catalog", "1.1.0", signer: stranger));

        ResultAssert.Fails(rejected, CloudErrorCodes.PackageSignatureRejected);
        Assert.Contains("UnknownKey", rejected.Error!.Message);
    }

    [Fact]
    public async Task Signature_ForgedWithTheTrustedKeyId_IsRejected()
    {
        using var w = new CloudWorld(trustPublisher: true);
        await w.NewModuleAsync("catalog");
        using var forger = Es256Signer.GenerateEphemeral(CloudWorld.PublisherKeyId); // same KeyId, different key

        var r = await w.PublishAsync(w.MakePackage("catalog", "1.0.0", signer: forger));

        ResultAssert.Fails(r, CloudErrorCodes.PackageSignatureRejected);
        Assert.Contains("InvalidSignature", r.Error!.Message);
    }

    [Fact]
    public async Task Signature_CheckIsOptional_WhenNoTrustedKeysAreConfigured_ClientsStillVerify()
    {
        await _w.NewModuleAsync("catalog");
        using var anyone = Es256Signer.GenerateEphemeral("whoever");

        ResultAssert.Ok(await _w.PublishAsync(_w.MakePackage("catalog", "1.0.0", signer: anyone)));
    }

    [Fact]
    public async Task Stored_Manifest_StillVerifies_AfterTheDatabaseRoundTrip()
    {
        var dto = await _w.PublishOkAsync("catalog", "1.0.0");

        var served = _w.Get<IPackageRepository>().Find(dto.PackageId)!;
        using var verifier = new Es256Verifier([_w.PublisherKey.ToTrustedKey()]);
        var check = verifier.Verify(served.SignedManifest.KeyId, served.SignedManifest.Algorithm,
            Convert.FromBase64String(served.SignedManifest.Manifest), Convert.FromBase64String(served.SignedManifest.Signature));

        Assert.Equal(SignatureCheck.Valid, check);
        Assert.Equal(dto.Sha256, served.Sha256);
        Assert.Equal(dto.SizeBytes, served.SizeBytes);
    }

    // ---- lifecycle ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithdrawAndRestore_FollowTheStateMachine()
    {
        var p = await _w.PublishOkAsync("catalog", "1.0.0");
        _w.Clock.Advance(TimeSpan.FromHours(2));

        var withdrawn = ResultAssert.Ok(await _w.Packages.WithdrawAsync(_w.Actor, p.PackageId, "crashes on start"));
        Assert.Equal("Withdrawn", withdrawn.Status);
        Assert.Equal(CloudWorld.Start.AddHours(2), withdrawn.WithdrawnAt);
        ResultAssert.Fails(await _w.Packages.WithdrawAsync(_w.Actor, p.PackageId, null), CloudErrorCodes.InvalidState);

        var restored = ResultAssert.Ok(await _w.Packages.RestoreAsync(_w.Actor, p.PackageId, null));
        Assert.Equal("Published", restored.Status);
        Assert.Null(restored.WithdrawnAt);
        ResultAssert.Fails(await _w.Packages.RestoreAsync(_w.Actor, p.PackageId, null), CloudErrorCodes.InvalidState);

        ResultAssert.Fails(await _w.Packages.WithdrawAsync(_w.Actor, Guid.NewGuid(), null), CloudErrorCodes.NotFound);
        ResultAssert.Fails(await _w.Packages.GetAsync(Guid.NewGuid()), CloudErrorCodes.NotFound);
    }

    [Fact]
    public async Task List_FiltersAndOrdersNewestVersionFirst()
    {
        await _w.PublishOkAsync("catalog", "1.2.0");
        var old = await _w.PublishOkAsync("catalog", "1.10.0");
        await _w.PublishOkAsync("inventory", "3.0.0");
        await _w.Packages.WithdrawAsync(_w.Actor, old.PackageId, null);

        Assert.Equal(["1.10.0", "1.2.0", "3.0.0"], (await _w.Packages.ListAsync(null, null)).Select(p => p.Version));
        Assert.Equal(["1.10.0", "1.2.0"], (await _w.Packages.ListAsync("CATALOG", null)).Select(p => p.Version));
        Assert.Equal(["1.10.0"], (await _w.Packages.ListAsync(null, "withdrawn")).Select(p => p.Version));
        Assert.Equal(2, (await _w.Packages.ListAsync(null, "published")).Count);
        Assert.Equal(3, (await _w.Packages.ListAsync(null, "bogus")).Count);
    }

    [Fact]
    public async Task PublishWithdrawRestore_AreAudited()
    {
        var p = await _w.PublishOkAsync("catalog", "1.0.0");
        _w.Clock.Advance(TimeSpan.FromMinutes(1));
        await _w.Packages.WithdrawAsync(_w.Actor, p.PackageId, "bad build");
        _w.Clock.Advance(TimeSpan.FromMinutes(1));
        await _w.Packages.RestoreAsync(_w.Actor, p.PackageId, null);

        var audit = await _w.Operations.QueryAuditAsync(new AuditFilter(EntityType: "package"), null, null);

        Assert.Equal(["package.restore", "package.withdraw", "package.publish"], audit.Items.Select(a => a.Action));
        Assert.Contains("bad build", audit.Items[1].Summary);
    }

    // ---- discovery over the durable catalog -----------------------------------------------------------------------

    [Fact]
    public async Task Discovery_OffersOnlyPublishedPackages_AndNewestFirst()
    {
        await _w.PublishOkAsync("catalog", "1.1.0");
        var broken = await _w.PublishOkAsync("catalog", "1.2.0");
        var discovery = new UpdateDiscoveryService(_w.Get<IPackageRepository>());
        var request = new UpdateCheckRequest("1.0.0", "net10.0", [new InstalledTarget("catalog", "1.0.0")]);

        Assert.Equal("1.2.0", Assert.Single(discovery.Check(request).Updates).Version);

        await _w.Packages.WithdrawAsync(_w.Actor, broken.PackageId, null);
        Assert.Equal("1.1.0", Assert.Single(discovery.Check(request).Updates).Version);

        await _w.Packages.RestoreAsync(_w.Actor, broken.PackageId, null);
        Assert.Equal("1.2.0", Assert.Single(discovery.Check(request).Updates).Version);
    }

    [Fact]
    public async Task WithdrawnPackage_CannotBeDownloaded_ButRestoredOneCan()
    {
        var p = await _w.PublishOkAsync("catalog", "1.0.0");
        var repository = _w.Get<IPackageRepository>();

        using (var stream = repository.OpenRead(p.PackageId))
            Assert.NotNull(stream);

        await _w.Packages.WithdrawAsync(_w.Actor, p.PackageId, null);
        Assert.Null(repository.OpenRead(p.PackageId));
        Assert.Null(repository.Find(p.PackageId));
        Assert.Empty(repository.List());

        await _w.Packages.RestoreAsync(_w.Actor, p.PackageId, null);
        using var again = repository.OpenRead(p.PackageId);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task UnknownPackage_IsNotServed()
    {
        var repository = _w.Get<IPackageRepository>();

        Assert.Null(repository.OpenRead(Guid.NewGuid()));
        Assert.Null(repository.Find(Guid.NewGuid()));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Packages_SurviveAServerRestart()
    {
        var p = await _w.PublishOkAsync("catalog", "1.0.0");

        using var restarted = new CloudWorld(new Dictionary<string, string?>(_w.Settings));

        Assert.Equal("1.0.0", ResultAssert.Ok(await restarted.Packages.GetAsync(p.PackageId)).Version);
        using var stream = restarted.Get<IPackageRepository>().OpenRead(p.PackageId);
        Assert.NotNull(stream);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>Re-packs a valid package with the SAME (signed) envelope but a different payload.</summary>
    private string Rewrite(string source, (string Path, string Content)[] payload, bool expectListedPayload = true)
    {
        var opened = PackageReader.Open(source);
        Assert.True(opened.IsSuccess);
        using var contents = opened.Contents!;
        Assert.True(expectListedPayload ? contents.PayloadPaths.Count == 1 : true);

        var target = Path.Combine(_w.Dir, "out", "rewritten-" + Guid.NewGuid().ToString("N") + PackageFormat.Extension);
        using var output = File.Create(target);
        PackageWriter.Write(output, contents.Envelope,
            payload.Select(p => new PackageFileSource(p.Path, () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(p.Content)))));
        return target;
    }
}
