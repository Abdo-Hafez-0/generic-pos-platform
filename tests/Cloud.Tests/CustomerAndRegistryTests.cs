using AdminPortal.Application;
using Cloud.Contracts;
using Cloud.Contracts.Admin;

namespace Cloud.Tests;

public sealed class AdminKeyTests
{
    private static AdminKeyEntry Entry(string name, string key) => new(name, AdminKeys.Hash(key));

    [Fact]
    public void ValidKey_Authenticates_AsItsName()
    {
        var auth = new AdminKeyAuthenticator([Entry("alice", "k1"), Entry("bob", "k2")]);

        Assert.Equal("alice", auth.Authenticate("k1")!.Name);
        Assert.Equal("bob", auth.Authenticate("k2")!.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wrong")]
    [InlineData("K1")]
    public void MissingOrWrongKey_IsRejected(string? presented)
    {
        var auth = new AdminKeyAuthenticator([Entry("alice", "k1")]);

        Assert.Null(auth.Authenticate(presented));
    }

    [Fact]
    public void NoConfiguredKeys_MeansNobodyGetsIn()
    {
        var auth = new AdminKeyAuthenticator([]);

        Assert.Equal(0, auth.KeyCount);
        Assert.Null(auth.Authenticate("anything"));
    }

    [Fact]
    public void MalformedEntries_AreIgnored_FailClosed()
    {
        var auth = new AdminKeyAuthenticator([
            new AdminKeyEntry("", AdminKeys.Hash("k")),
            new AdminKeyEntry("short", "abc"),
            new AdminKeyEntry("nothex", new string('z', 64)),
            Entry("good", "k")]);

        Assert.Equal(1, auth.KeyCount);
        Assert.Equal("good", auth.Authenticate("k")!.Name);
    }

    [Fact]
    public void PresentedKey_IsTrimmed_AndStoredOnlyAsHash()
    {
        var key = AdminKeys.Generate();
        var entry = Entry("alice", key);

        Assert.StartsWith(AdminKeys.Prefix, key);
        Assert.DoesNotContain(key, entry.Sha256);
        Assert.Equal(64, entry.Sha256.Length);
        Assert.Equal("alice", new AdminKeyAuthenticator([entry]).Authenticate("  " + key + " ")!.Name);
    }

    [Fact]
    public void GeneratedKeys_AreUnique()
        => Assert.NotEqual(AdminKeys.Generate(), AdminKeys.Generate());
}

public sealed class CustomerAdminTests : IDisposable
{
    private readonly CloudWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Fact]
    public async Task Create_Valid_StoresAndReturnsCustomer()
    {
        var r = await _w.Customers.CreateAsync(_w.Actor, new CustomerRequest("  Acme  ", " Pat ", "pat@acme.test", " 555 ", "vip"));

        var c = ResultAssert.Ok(r);
        Assert.NotEqual(Guid.Empty, c.Id);
        Assert.Equal("Acme", c.Name);
        Assert.Equal("Pat", c.ContactName);
        Assert.Equal("555", c.Phone);
        Assert.True(c.IsActive);
        Assert.Equal(CloudWorld.Start, c.CreatedAt);
        Assert.Equal(c, ResultAssert.Ok(await _w.Customers.GetAsync(c.Id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_WithoutName_IsRejected(string name)
        => ResultAssert.Fails(await _w.Customers.CreateAsync(_w.Actor, new CustomerRequest(name, null, null, null, null)), CloudErrorCodes.Validation);

    [Fact]
    public async Task Create_NullRequest_IsRejected()
        => ResultAssert.Fails(await _w.Customers.CreateAsync(_w.Actor, null!), CloudErrorCodes.Validation);

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@")]
    [InlineData("@b.test")]
    [InlineData("a b@c.test")]
    [InlineData("a@b@c.test")]
    public async Task Create_InvalidEmail_IsRejected(string email)
        => ResultAssert.Fails(await _w.Customers.CreateAsync(_w.Actor, new CustomerRequest("X", null, email, null, null)), CloudErrorCodes.Validation);

    [Fact]
    public async Task Create_TooLongFields_AreRejected()
    {
        ResultAssert.Fails(await _w.Customers.CreateAsync(_w.Actor, new CustomerRequest(new string('n', 201), null, null, null, null)), CloudErrorCodes.Validation);
        ResultAssert.Fails(await _w.Customers.CreateAsync(_w.Actor, new CustomerRequest("X", null, null, null, new string('n', 2001))), CloudErrorCodes.Validation);
    }

    [Fact]
    public async Task Create_DuplicateName_IsConflict_CaseInsensitively()
    {
        await _w.NewCustomerAsync("Acme");

        ResultAssert.Fails(await _w.Customers.CreateAsync(_w.Actor, new CustomerRequest("ACME", null, null, null, null)), CloudErrorCodes.Conflict);
    }

    [Fact]
    public async Task Update_ChangesFields_AndStampsTime()
    {
        var c = await _w.NewCustomerAsync("Acme");
        _w.Clock.Advance(TimeSpan.FromHours(1));

        var updated = ResultAssert.Ok(await _w.Customers.UpdateAsync(_w.Actor, c.Id, new CustomerRequest("Acme Ltd", null, "new@acme.test", null, "n")));

        Assert.Equal("Acme Ltd", updated.Name);
        Assert.Equal("new@acme.test", updated.Email);
        Assert.Null(updated.ContactName);
        Assert.Equal(c.CreatedAt, updated.CreatedAt);
        Assert.Equal(CloudWorld.Start.AddHours(1), updated.UpdatedAt);
    }

    [Fact]
    public async Task Update_KeepingOwnName_IsAllowed_ButTakingAnothersIsConflict()
    {
        var a = await _w.NewCustomerAsync("Alpha");
        await _w.NewCustomerAsync("Beta");

        ResultAssert.Ok(await _w.Customers.UpdateAsync(_w.Actor, a.Id, new CustomerRequest("alpha", "x", null, null, null)));
        ResultAssert.Fails(await _w.Customers.UpdateAsync(_w.Actor, a.Id, new CustomerRequest("BETA", null, null, null, null)), CloudErrorCodes.Conflict);
    }

    [Fact]
    public async Task Update_AndGet_Unknown_AreNotFound()
    {
        ResultAssert.Fails(await _w.Customers.UpdateAsync(_w.Actor, Guid.NewGuid(), new CustomerRequest("X", null, null, null, null)), CloudErrorCodes.NotFound);
        ResultAssert.Fails(await _w.Customers.GetAsync(Guid.NewGuid()), CloudErrorCodes.NotFound);
        ResultAssert.Fails(await _w.Customers.DeactivateAsync(_w.Actor, Guid.NewGuid()), CloudErrorCodes.NotFound);
    }

    [Fact]
    public async Task DeactivateReactivate_FollowTheStateMachine()
    {
        var c = await _w.NewCustomerAsync();

        Assert.False(ResultAssert.Ok(await _w.Customers.DeactivateAsync(_w.Actor, c.Id)).IsActive);
        ResultAssert.Fails(await _w.Customers.DeactivateAsync(_w.Actor, c.Id), CloudErrorCodes.InvalidState);
        Assert.True(ResultAssert.Ok(await _w.Customers.ReactivateAsync(_w.Actor, c.Id)).IsActive);
        ResultAssert.Fails(await _w.Customers.ReactivateAsync(_w.Actor, c.Id), CloudErrorCodes.InvalidState);
    }

    [Fact]
    public async Task List_FiltersSearchesAndPages()
    {
        foreach (var n in new[] { "Alpha Store", "Beta Store", "Gamma Shop" })
            await _w.NewCustomerAsync(n);
        var inactive = await _w.NewCustomerAsync("Delta Store");
        await _w.Customers.DeactivateAsync(_w.Actor, inactive.Id);

        var active = await _w.Customers.ListAsync(null, false, null, null);
        var all = await _w.Customers.ListAsync(null, true, null, null);
        var store = await _w.Customers.ListAsync("store", true, null, null);
        var byEmail = await _w.Customers.ListAsync("pat@acme", true, null, null);
        var paged = await _w.Customers.ListAsync(null, true, 2, 2);

        Assert.Equal(3, active.Total);
        Assert.Equal(["Alpha Store", "Beta Store", "Gamma Shop"], active.Items.Select(c => c.Name));
        Assert.Equal(4, all.Total);
        Assert.Equal(3, store.Total);
        Assert.Equal(4, byEmail.Total);
        Assert.Equal(2, paged.Items.Count);
        Assert.Equal((2, 2, 4), (paged.Page, paged.PageSize, paged.Total));
    }

    [Fact]
    public async Task List_PagingIsClamped()
    {
        await _w.NewCustomerAsync();

        var r = await _w.Customers.ListAsync(null, false, -5, 100_000);

        Assert.Equal(1, r.Page);
        Assert.Equal(Paging.MaxPageSize, r.PageSize);
    }

    [Fact]
    public async Task EveryChange_IsAudited_WithTheActor()
    {
        var c = await _w.NewCustomerAsync("Acme");
        _w.Clock.Advance(TimeSpan.FromMinutes(1));
        await _w.Customers.UpdateAsync(_w.Actor, c.Id, new CustomerRequest("Acme 2", null, null, null, null));
        _w.Clock.Advance(TimeSpan.FromMinutes(1));
        await _w.Customers.DeactivateAsync(_w.Actor, c.Id);

        var audit = await _w.Operations.QueryAuditAsync(new AuditFilter(EntityType: "customer", EntityId: c.Id.ToString()), null, null);

        Assert.Equal(["customer.deactivate", "customer.update", "customer.create"], audit.Items.Select(a => a.Action));
        Assert.All(audit.Items, a => Assert.Equal("tester", a.Actor));
    }

    [Fact]
    public async Task Customers_Persist_AcrossAServerRestart()
    {
        var c = await _w.NewCustomerAsync("Durable");

        using var restarted = new CloudWorld(new Dictionary<string, string?>(_w.Settings));

        Assert.Equal("Durable", ResultAssert.Ok(await restarted.Customers.GetAsync(c.Id)).Name);
    }
}

public sealed class ModuleRegistryTests : IDisposable
{
    private readonly CloudWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Fact]
    public async Task Register_Valid_NormalizesTheId()
    {
        var m = ResultAssert.Ok(await _w.Modules.RegisterAsync(_w.Actor, new RegisterModuleRequest("  Cash-Management ", "Cash Management", "Drawer sessions", "Standard")));

        Assert.Equal("cash-management", m.ModuleId);
        Assert.Equal("Standard", m.Category);
        Assert.True(m.IsActive);
        Assert.Null(m.LatestVersion);
        Assert.Equal(0, m.PublishedVersions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bad/slash")]
    [InlineData("core")]
    [InlineData("CORE")]
    public async Task Register_InvalidId_IsRejected(string id)
        => ResultAssert.Fails(await _w.Modules.RegisterAsync(_w.Actor, new RegisterModuleRequest(id, "X", null, null)), CloudErrorCodes.Validation);

    [Fact]
    public async Task Register_MissingNameOrBadCategory_IsRejected()
    {
        ResultAssert.Fails(await _w.Modules.RegisterAsync(_w.Actor, new RegisterModuleRequest("m", " ", null, null)), CloudErrorCodes.Validation);
        ResultAssert.Fails(await _w.Modules.RegisterAsync(_w.Actor, new RegisterModuleRequest("m", "M", null, "Premium")), CloudErrorCodes.Validation);
        ResultAssert.Fails(await _w.Modules.RegisterAsync(_w.Actor, null!), CloudErrorCodes.Validation);
    }

    [Fact]
    public async Task Register_DefaultsToStandard_AndAcceptsOptional()
    {
        Assert.Equal("Standard", ResultAssert.Ok(await _w.Modules.RegisterAsync(_w.Actor, new RegisterModuleRequest("a", "A", null, null))).Category);
        Assert.Equal("Optional", ResultAssert.Ok(await _w.Modules.RegisterAsync(_w.Actor, new RegisterModuleRequest("b", "B", null, "optional"))).Category);
    }

    [Fact]
    public async Task Register_Duplicate_IsConflict()
    {
        await _w.NewModuleAsync("accounting");

        ResultAssert.Fails(await _w.Modules.RegisterAsync(_w.Actor, new RegisterModuleRequest("ACCOUNTING", "Again", null, null)), CloudErrorCodes.Conflict);
    }

    [Fact]
    public async Task Update_ChangesNameAndDescription()
    {
        await _w.NewModuleAsync("loyalty");

        var m = ResultAssert.Ok(await _w.Modules.UpdateAsync(_w.Actor, "LOYALTY", new UpdateModuleRequest("Loyalty Points", "new text")));

        Assert.Equal("Loyalty Points", m.DisplayName);
        Assert.Equal("new text", m.Description);
        ResultAssert.Fails(await _w.Modules.UpdateAsync(_w.Actor, "missing", new UpdateModuleRequest("X", null)), CloudErrorCodes.NotFound);
        ResultAssert.Fails(await _w.Modules.UpdateAsync(_w.Actor, "loyalty", new UpdateModuleRequest("", null)), CloudErrorCodes.Validation);
    }

    [Fact]
    public async Task RetireAndReactivate_FollowTheStateMachine_AndListHidesRetired()
    {
        await _w.NewModuleAsync("old-module");
        await _w.NewModuleAsync("new-module");

        Assert.False(ResultAssert.Ok(await _w.Modules.RetireAsync(_w.Actor, "old-module")).IsActive);
        ResultAssert.Fails(await _w.Modules.RetireAsync(_w.Actor, "old-module"), CloudErrorCodes.InvalidState);

        Assert.Equal(["new-module"], (await _w.Modules.ListAsync(false)).Select(m => m.ModuleId));
        Assert.Equal(["new-module", "old-module"], (await _w.Modules.ListAsync(true)).Select(m => m.ModuleId));

        Assert.True(ResultAssert.Ok(await _w.Modules.ReactivateAsync(_w.Actor, "old-module")).IsActive);
        ResultAssert.Fails(await _w.Modules.ReactivateAsync(_w.Actor, "old-module"), CloudErrorCodes.InvalidState);
        ResultAssert.Fails(await _w.Modules.RetireAsync(_w.Actor, "nope"), CloudErrorCodes.NotFound);
    }

    [Fact]
    public async Task Detail_AndList_ShowPublishedVersions_FromThePackageCatalog()
    {
        await _w.PublishOkAsync("catalog", "1.0.0");
        var v2 = await _w.PublishOkAsync("catalog", "1.10.0");
        await _w.PublishOkAsync("catalog", "1.9.0");
        await _w.Packages.WithdrawAsync(_w.Actor, v2.PackageId, "bad build");

        var detail = ResultAssert.Ok(await _w.Modules.GetAsync("catalog"));
        var listed = Assert.Single(await _w.Modules.ListAsync(false));

        Assert.Equal(2, detail.Module.PublishedVersions);
        Assert.Equal("1.9.0", detail.Module.LatestVersion);
        Assert.Equal(3, detail.Packages.Count);
        Assert.Equal(detail.Module.LatestVersion, listed.LatestVersion);
        ResultAssert.Fails(await _w.Modules.GetAsync("missing"), CloudErrorCodes.NotFound);
    }
}
