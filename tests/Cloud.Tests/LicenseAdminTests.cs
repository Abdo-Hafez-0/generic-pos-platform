using AdminPortal.Application;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using Cloud.Infrastructure.Persistence;
using LicenseServer.Application;
using Licensing.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cloud.Tests;

public sealed class LicenseAdminTests : IDisposable
{
    private readonly CloudWorld _w = new();

    public void Dispose() => _w.Dispose();

    private Task<ServiceResult<CreateLicenseResponse>> Create(string customerId, DateTimeOffset? from = null, DateTimeOffset? until = null,
        string[]? modules = null, string[]? features = null, string product = "genericpos")
        => _w.Licenses.CreateAsync(_w.Actor, new CreateLicenseRequest(
            customerId, product, from ?? CloudWorld.Start, until ?? CloudWorld.Start.AddYears(1), modules, features));

    [Fact]
    public async Task Create_Valid_ReturnsTheActivationKeyOnce()
    {
        await _w.NewModuleAsync("catalog");
        var customer = await _w.NewCustomerAsync();

        var r = ResultAssert.Ok(await Create(customer.Id.ToString(), modules: ["Catalog", "catalog"], features: ["advancedreports", "AdvancedReports"]));

        Assert.Matches(@"^[A-HJ-NP-Z2-9]{5}(-[A-HJ-NP-Z2-9]{5}){4}$", r.ActivationKey);
        Assert.Equal(customer.Id.ToString(), r.License.CustomerId);
        Assert.Equal("Active", r.License.Status);
        Assert.Equal(["catalog"], r.License.Modules);
        Assert.Equal(["advancedreports"], r.License.Features);
        Assert.Null(r.License.InstallationId);
        Assert.False(r.License.HasBackupAccess);
        Assert.Equal(0, r.License.Version);
    }

    [Fact]
    public async Task Create_StoresOnlyTheHashOfTheActivationKey()
    {
        var license = await _w.NewLicenseAsync();

        var factory = _w.Get<IDbContextFactory<CloudDbContext>>();
        await using var db = factory.CreateDbContext();
        var stored = await db.Database.SqlQueryRaw<string>("SELECT ActivationKeyHash AS Value FROM lic_Licenses").ToListAsync();

        Assert.Equal([ActivationKeys.Hash(license.ActivationKey)], stored);
        Assert.DoesNotContain(license.ActivationKey, stored[0]);
        foreach (var file in new[] { _w.DatabaseFile, _w.DatabaseFile + "-wal" }.Where(File.Exists))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
            Assert.DoesNotContain(license.ActivationKey, reader.ReadToEnd());
        }
    }

    [Fact]
    public async Task Create_RejectsBadInput()
    {
        var customer = await _w.NewCustomerAsync();
        var id = customer.Id.ToString();

        ResultAssert.Fails(await _w.Licenses.CreateAsync(_w.Actor, null!), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Create("not-a-guid"), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Create(id, product: " "), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Create(id, product: new string('p', 101)), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Create(id, from: CloudWorld.Start, until: CloudWorld.Start), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Create(id, from: CloudWorld.Start.AddYears(-2), until: CloudWorld.Start.AddDays(-1)), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Create(id, features: ["has space"]), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Create(id, features: [""]), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Create(id, modules: ["Bad Module"]), CloudErrorCodes.Validation);
    }

    [Fact]
    public async Task Create_UnknownCustomer_IsNotFound_AndInactiveCustomer_IsInvalidState()
    {
        ResultAssert.Fails(await Create(Guid.NewGuid().ToString()), CloudErrorCodes.NotFound);

        var customer = await _w.NewCustomerAsync();
        await _w.Customers.DeactivateAsync(_w.Actor, customer.Id);
        ResultAssert.Fails(await Create(customer.Id.ToString()), CloudErrorCodes.InvalidState);
    }

    [Fact]
    public async Task Create_WithUnregisteredOrRetiredModule_IsRejected()
    {
        var customer = await _w.NewCustomerAsync();
        await _w.NewModuleAsync("old");
        await _w.Modules.RetireAsync(_w.Actor, "old");

        var unknown = await Create(customer.Id.ToString(), modules: ["accounting"]);
        var retired = await Create(customer.Id.ToString(), modules: ["old"]);

        ResultAssert.Fails(unknown, CloudErrorCodes.Validation);
        Assert.Contains("accounting", unknown.Error!.Message);
        ResultAssert.Fails(retired, CloudErrorCodes.Validation);
    }

    [Fact]
    public async Task Get_And_List_ReturnLicenses_WithFilters()
    {
        var a = await _w.NewLicenseAsync(customerName: "Alpha");
        var b = await _w.NewLicenseAsync(customerName: "Beta");
        await _w.Licenses.SuspendAsync(_w.Actor, b.License.LicenseId, null);

        Assert.Equal(a.License.LicenseId, ResultAssert.Ok(await _w.Licenses.GetAsync(a.License.LicenseId)).LicenseId);
        ResultAssert.Fails(await _w.Licenses.GetAsync(Guid.NewGuid()), CloudErrorCodes.NotFound);

        Assert.Equal(2, (await _w.Licenses.ListAsync(null, null, null, null)).Total);
        Assert.Equal([b.License.LicenseId], (await _w.Licenses.ListAsync(null, "suspended", null, null)).Items.Select(l => l.LicenseId));
        Assert.Equal([a.License.LicenseId], (await _w.Licenses.ListAsync(a.License.CustomerId, null, null, null)).Items.Select(l => l.LicenseId));
        Assert.Equal(2, (await _w.Licenses.ListAsync(null, "not-a-status", null, null)).Total);
        Assert.Single((await _w.Licenses.ListAsync(null, null, 2, 1)).Items);
    }

    [Fact]
    public async Task SuspendReinstateRevoke_FollowTheStateMachine()
    {
        var id = (await _w.NewLicenseAsync()).License.LicenseId;

        ResultAssert.Fails(await _w.Licenses.ReinstateAsync(_w.Actor, id, null), CloudErrorCodes.InvalidState);
        Assert.Equal("Suspended", ResultAssert.Ok(await _w.Licenses.SuspendAsync(_w.Actor, id, "non-payment")).Status);
        ResultAssert.Fails(await _w.Licenses.SuspendAsync(_w.Actor, id, null), CloudErrorCodes.InvalidState);
        Assert.Equal("Active", ResultAssert.Ok(await _w.Licenses.ReinstateAsync(_w.Actor, id, null)).Status);
        Assert.Equal("Revoked", ResultAssert.Ok(await _w.Licenses.RevokeAsync(_w.Actor, id, "fraud")).Status);

        // Revocation is terminal.
        ResultAssert.Fails(await _w.Licenses.RevokeAsync(_w.Actor, id, null), CloudErrorCodes.InvalidState);
        ResultAssert.Fails(await _w.Licenses.ReinstateAsync(_w.Actor, id, null), CloudErrorCodes.InvalidState);
        ResultAssert.Fails(await _w.Licenses.SuspendAsync(_w.Actor, id, null), CloudErrorCodes.InvalidState);
        ResultAssert.Fails(await _w.Licenses.SuspendAsync(_w.Actor, Guid.NewGuid(), null), CloudErrorCodes.NotFound);
    }

    [Fact]
    public async Task SuspendedLicense_CanBeRevoked()
    {
        var id = (await _w.NewLicenseAsync()).License.LicenseId;
        await _w.Licenses.SuspendAsync(_w.Actor, id, null);

        Assert.Equal("Revoked", ResultAssert.Ok(await _w.Licenses.RevokeAsync(_w.Actor, id, null)).Status);
    }

    [Fact]
    public async Task Extend_MovesTheExpiryForward_Only()
    {
        var license = await _w.NewLicenseAsync();
        var id = license.License.LicenseId;
        var later = license.License.ValidUntil.AddMonths(6);

        Assert.Equal(later, ResultAssert.Ok(await _w.Licenses.ExtendAsync(_w.Actor, id, new ExtendLicenseRequest(later))).ValidUntil);
        ResultAssert.Fails(await _w.Licenses.ExtendAsync(_w.Actor, id, new ExtendLicenseRequest(later)), CloudErrorCodes.Validation);
        ResultAssert.Fails(await _w.Licenses.ExtendAsync(_w.Actor, id, new ExtendLicenseRequest(later.AddDays(-400))), CloudErrorCodes.Validation);
        ResultAssert.Fails(await _w.Licenses.ExtendAsync(_w.Actor, id, null!), CloudErrorCodes.Validation);
        ResultAssert.Fails(await _w.Licenses.ExtendAsync(_w.Actor, Guid.NewGuid(), new ExtendLicenseRequest(later)), CloudErrorCodes.NotFound);

        await _w.Licenses.RevokeAsync(_w.Actor, id, null);
        ResultAssert.Fails(await _w.Licenses.ExtendAsync(_w.Actor, id, new ExtendLicenseRequest(later.AddYears(1))), CloudErrorCodes.InvalidState);
    }

    [Fact]
    public async Task SetEntitlements_ReplacesModulesAndFeatures()
    {
        var license = await _w.NewLicenseAsync(modules: ["catalog"], features: ["a"]);
        await _w.NewModuleAsync("accounting");

        var r = ResultAssert.Ok(await _w.Licenses.SetEntitlementsAsync(_w.Actor, license.License.LicenseId,
            new SetEntitlementsRequest(["catalog", "accounting"], ["b"])));

        Assert.Equal(["catalog", "accounting"], r.Modules);
        Assert.Equal(["b"], r.Features);
    }

    [Fact]
    public async Task SetEntitlements_AddingAnUnregisteredModule_IsRejected_ButRemovingAlwaysWorks()
    {
        var license = await _w.NewLicenseAsync(modules: ["catalog"]);
        var id = license.License.LicenseId;

        ResultAssert.Fails(await _w.Licenses.SetEntitlementsAsync(_w.Actor, id, new SetEntitlementsRequest(["catalog", "ghost"], null)), CloudErrorCodes.Validation);

        // Retiring a module does not strand licenses that already have it: they can keep it, and can drop it.
        await _w.Modules.RetireAsync(_w.Actor, "catalog");
        Assert.Equal(["catalog"], ResultAssert.Ok(await _w.Licenses.SetEntitlementsAsync(_w.Actor, id, new SetEntitlementsRequest(["catalog"], ["x"]))).Modules);
        Assert.Empty(ResultAssert.Ok(await _w.Licenses.SetEntitlementsAsync(_w.Actor, id, new SetEntitlementsRequest([], []))).Modules);
        ResultAssert.Fails(await _w.Licenses.SetEntitlementsAsync(_w.Actor, id, new SetEntitlementsRequest(["catalog"], null)), CloudErrorCodes.Validation);
    }

    [Fact]
    public async Task SetEntitlements_InvalidInput_UnknownLicense_AndRevoked()
    {
        var id = (await _w.NewLicenseAsync()).License.LicenseId;

        ResultAssert.Fails(await _w.Licenses.SetEntitlementsAsync(_w.Actor, id, null!), CloudErrorCodes.Validation);
        ResultAssert.Fails(await _w.Licenses.SetEntitlementsAsync(_w.Actor, id, new SetEntitlementsRequest(["Bad Id"], null)), CloudErrorCodes.Validation);
        ResultAssert.Fails(await _w.Licenses.SetEntitlementsAsync(_w.Actor, Guid.NewGuid(), new SetEntitlementsRequest([], [])), CloudErrorCodes.NotFound);

        await _w.Licenses.RevokeAsync(_w.Actor, id, null);
        ResultAssert.Fails(await _w.Licenses.SetEntitlementsAsync(_w.Actor, id, new SetEntitlementsRequest([], ["x"])), CloudErrorCodes.InvalidState);
    }

    [Fact]
    public async Task ReleaseInstallation_UnbindsTheLicense_SoAnotherInstallationCanActivate()
    {
        var license = await _w.NewLicenseAsync();
        var first = await _w.ActivateAsync(license);

        ResultAssert.Fails(await _w.Licenses.ReleaseInstallationAsync(_w.Actor, Guid.NewGuid()), CloudErrorCodes.NotFound);

        var anotherBefore = await _w.Issuance().ActivateAsync(new ActivationRequest(license.ActivationKey, Guid.NewGuid(), "genericpos"));
        Assert.Equal(LicenseErrorCodes.AlreadyActivated, anotherBefore.ErrorCode);

        var released = ResultAssert.Ok(await _w.Licenses.ReleaseInstallationAsync(_w.Actor, license.License.LicenseId));
        Assert.Null(released.InstallationId);
        Assert.Null(released.ActivatedAt);
        ResultAssert.Fails(await _w.Licenses.ReleaseInstallationAsync(_w.Actor, license.License.LicenseId), CloudErrorCodes.InvalidState);

        var second = Guid.NewGuid();
        Assert.True((await _w.Issuance().ActivateAsync(new ActivationRequest(license.ActivationKey, second, "genericpos"))).IsSuccess);

        // The old installation can no longer renew.
        var oldRenewal = await _w.Issuance().RenewAsync(new RenewalRequest(license.License.LicenseId, first, 1));
        Assert.Equal(LicenseErrorCodes.InstallationMismatch, oldRenewal.ErrorCode);
    }

    [Fact]
    public async Task Installations_ListsOnlyBoundLicenses()
    {
        var bound = await _w.NewLicenseAsync(customerName: "Alpha");
        await _w.NewLicenseAsync(customerName: "Beta");
        var installation = await _w.ActivateAsync(bound);

        var list = await _w.Licenses.ListInstallationsAsync(null, null, null);

        var item = Assert.Single(list.Items);
        Assert.Equal(installation, item.InstallationId);
        Assert.Equal(bound.License.LicenseId, item.LicenseId);
        Assert.Equal("Active", item.LicenseStatus);
        Assert.Equal(CloudWorld.Start, item.ActivatedAt);
        Assert.Equal(1, list.Total);
        Assert.Empty((await _w.Licenses.ListInstallationsAsync(Guid.NewGuid().ToString(), null, null)).Items);
    }

    [Fact]
    public async Task BackupToken_IsIssuedOnce_Rotates_AndCanBeRevoked()
    {
        var license = await _w.NewLicenseAsync();
        var id = license.License.LicenseId;

        var first = ResultAssert.Ok(await _w.Licenses.IssueBackupTokenAsync(_w.Actor, id)).Token;
        Assert.StartsWith("gpb_", first);
        Assert.True(ResultAssert.Ok(await _w.Licenses.GetAsync(id)).HasBackupAccess);

        var second = ResultAssert.Ok(await _w.Licenses.IssueBackupTokenAsync(_w.Actor, id)).Token;
        Assert.NotEqual(first, second);

        Assert.True((await _w.Licenses.RevokeBackupTokenAsync(_w.Actor, id)).IsSuccess);
        Assert.False(ResultAssert.Ok(await _w.Licenses.GetAsync(id)).HasBackupAccess);
        ResultAssert.Fails(await _w.Licenses.RevokeBackupTokenAsync(_w.Actor, id), CloudErrorCodes.NotFound);
        ResultAssert.Fails(await _w.Licenses.IssueBackupTokenAsync(_w.Actor, Guid.NewGuid()), CloudErrorCodes.NotFound);
    }

    [Fact]
    public async Task BackupToken_CannotBeIssuedForARevokedLicense()
    {
        var id = (await _w.NewLicenseAsync()).License.LicenseId;
        await _w.Licenses.RevokeAsync(_w.Actor, id, null);

        ResultAssert.Fails(await _w.Licenses.IssueBackupTokenAsync(_w.Actor, id), CloudErrorCodes.InvalidState);
    }

    [Fact]
    public async Task EveryLicenseChange_IsAudited_AndNoAuditEntryContainsASecret()
    {
        var license = await _w.NewLicenseAsync();
        var id = license.License.LicenseId;
        var token = ResultAssert.Ok(await _w.Licenses.IssueBackupTokenAsync(_w.Actor, id)).Token;
        _w.Clock.Advance(TimeSpan.FromMinutes(1));
        await _w.Licenses.SuspendAsync(_w.Actor, id, "late payment");

        var audit = await _w.Operations.QueryAuditAsync(new AuditFilter(EntityType: "license"), null, null);

        Assert.Contains(audit.Items, a => a.Action == "license.create");
        Assert.Contains(audit.Items, a => a.Action == "license.backup-token.issue");
        Assert.Contains(audit.Items, a => a.Action == "license.suspend" && a.Summary.Contains("late payment"));
        Assert.All(audit.Items, a =>
        {
            Assert.DoesNotContain(license.ActivationKey, a.Summary);
            Assert.DoesNotContain(token, a.Summary);
        });
    }

    [Fact]
    public async Task ConcurrentChange_IsDetected_AndNeverSilentlyOverwritten()
    {
        var license = await _w.NewLicenseAsync();
        var id = license.License.LicenseId;
        var activation = Guid.NewGuid();
        await _w.ActivateAsync(license, activation);

        // A renewal loads the license, then the vendor revokes it before the renewal saves.
        var stale = (await _w.LicenseRepository.FindByIdAsync(id))!;
        ResultAssert.Ok(await _w.Licenses.RevokeAsync(_w.Actor, id, "chargeback"));

        stale.Version++;
        await Assert.ThrowsAsync<LicenseConcurrencyException>(() => _w.LicenseRepository.SaveAsync(stale));

        Assert.Equal(LicenseStatusClaim.Revoked, (await _w.LicenseRepository.FindByIdAsync(id))!.Status);
    }
}

/// <summary>The Stage 6 issuance service running on the durable store administration writes to.</summary>
public sealed class IssuanceOnDurableStoreTests : IDisposable
{
    private readonly CloudWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Fact]
    public async Task AdminCreatedLicense_ActivatesAndRenews_WithASignedPayload()
    {
        var license = await _w.NewLicenseAsync(modules: ["pos", "catalog"], features: ["advancedreports"]);
        var installation = Guid.NewGuid();

        var activation = await _w.Issuance().ActivateAsync(new ActivationRequest(license.ActivationKey, installation, "genericpos"));
        Assert.True(activation.IsSuccess, activation.ErrorMessage);

        var payload = LicenseSerializer.TryParsePayload(activation.License!.Payload)!;
        Assert.Equal(license.License.LicenseId, payload.LicenseId);
        Assert.Equal(installation, payload.InstallationId);
        Assert.Equal(["pos", "catalog"], payload.Modules);
        Assert.Equal(["advancedreports"], payload.Features);
        Assert.Equal(1, payload.LicenseVersion);

        _w.Clock.Advance(TimeSpan.FromDays(10));
        var renewal = await _w.Issuance().RenewAsync(new RenewalRequest(license.License.LicenseId, installation, 1));
        Assert.Equal(2, LicenseSerializer.TryParsePayload(renewal.License!.Payload)!.LicenseVersion);

        var view = ResultAssert.Ok(await _w.Licenses.GetAsync(license.License.LicenseId));
        Assert.Equal(2, view.Version);
        Assert.Equal(CloudWorld.Start, view.ActivatedAt);
        Assert.Equal(CloudWorld.Start.AddDays(10), view.LastIssuedAt);
    }

    [Fact]
    public async Task SuspensionAndRevocation_ReachTheClient_AtItsNextRenewal()
    {
        var license = await _w.NewLicenseAsync();
        var installation = await _w.ActivateAsync(license);
        var id = license.License.LicenseId;

        await _w.Licenses.SuspendAsync(_w.Actor, id, null);
        var suspended = await _w.Issuance().RenewAsync(new RenewalRequest(id, installation, 1));
        Assert.Equal(LicenseStatusClaim.Suspended, LicenseSerializer.TryParsePayload(suspended.License!.Payload)!.Status);

        await _w.Licenses.RevokeAsync(_w.Actor, id, null);
        var revoked = await _w.Issuance().RenewAsync(new RenewalRequest(id, installation, 2));
        Assert.Equal(LicenseStatusClaim.Revoked, LicenseSerializer.TryParsePayload(revoked.License!.Payload)!.Status);

        var blocked = await _w.Issuance().ActivateAsync(new ActivationRequest(license.ActivationKey, Guid.NewGuid(), "genericpos"));
        Assert.Equal(LicenseErrorCodes.Revoked, blocked.ErrorCode);
    }

    [Fact]
    public async Task ExtensionAndEntitlementChanges_AreIssuedAtNextRenewal()
    {
        var license = await _w.NewLicenseAsync(modules: ["catalog"]);
        var installation = await _w.ActivateAsync(license);
        await _w.NewModuleAsync("accounting");

        await _w.Licenses.SetEntitlementsAsync(_w.Actor, license.License.LicenseId, new SetEntitlementsRequest(["catalog", "accounting"], ["f1"]));
        var extended = license.License.ValidUntil.AddYears(1);
        await _w.Licenses.ExtendAsync(_w.Actor, license.License.LicenseId, new ExtendLicenseRequest(extended));

        var renewal = await _w.Issuance().RenewAsync(new RenewalRequest(license.License.LicenseId, installation, 1));
        var payload = LicenseSerializer.TryParsePayload(renewal.License!.Payload)!;

        Assert.Equal(["catalog", "accounting"], payload.Modules);
        Assert.Equal(["f1"], payload.Features);
        Assert.Equal(extended, payload.ValidUntil);
    }

    [Fact]
    public async Task UnknownKey_IsRefused_AndLookupIsByHash()
    {
        var license = await _w.NewLicenseAsync();

        var unknown = await _w.Issuance().ActivateAsync(new ActivationRequest("AAAAA-AAAAA-AAAAA-AAAAA-AAAAA", Guid.NewGuid(), "genericpos"));
        var stored = await _w.LicenseRepository.FindByActivationKeyAsync(license.ActivationKey);

        Assert.Equal(LicenseErrorCodes.NotFound, unknown.ErrorCode);
        Assert.Equal(license.License.LicenseId, stored!.LicenseId);
        Assert.Equal(ActivationKeys.Hash(license.ActivationKey), stored.ActivationKey);
    }

    [Fact]
    public async Task Licenses_SurviveAServerRestart()
    {
        var license = await _w.NewLicenseAsync();
        var installation = await _w.ActivateAsync(license);

        using var restarted = new CloudWorld(new Dictionary<string, string?>(_w.Settings));

        var renewal = await restarted.Issuance().RenewAsync(new RenewalRequest(license.License.LicenseId, installation, 1));
        Assert.True(renewal.IsSuccess, renewal.ErrorMessage);
        Assert.Equal(2, ResultAssert.Ok(await restarted.Licenses.GetAsync(license.License.LicenseId)).Version);
    }

    [Fact]
    public async Task DuplicateActivationKeyHash_IsRejectedByTheDatabase()
    {
        var license = await _w.NewLicenseAsync();
        var record = (await _w.LicenseRepository.FindByIdAsync(license.License.LicenseId))!;

        var duplicate = new LicenseRecord
        {
            LicenseId = Guid.NewGuid(), CustomerId = "x", ActivationKey = license.ActivationKey, ProductId = "genericpos",
            ValidFrom = record.ValidFrom, ValidUntil = record.ValidUntil, Modules = [], Features = []
        };

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _w.LicenseRepository.AddAsync(duplicate));
    }
}
