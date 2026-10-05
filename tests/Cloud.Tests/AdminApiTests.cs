using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cloud.Tests;

public sealed class AdminApiTests : IDisposable
{
    private readonly CloudWorld _world = new();
    private readonly WebApplicationFactory<AdminPortalApiMarker> _host;
    private readonly HttpClient _admin;

    public AdminApiTests()
    {
        _host = ApiHosting.Host<AdminPortalApiMarker>(_world.Settings);
        _admin = _host.CreateClient().WithBearer(CloudWorld.AdminKey);
    }

    public void Dispose()
    {
        _admin.Dispose();
        _host.Dispose();
        _world.Dispose();
    }

    private async Task<CustomerDto> CreateCustomer(string name = "Acme")
        => await (await _admin.PostAsJsonAsync("/api/admin/customers", new CustomerRequest(name, null, "a@b.test", null, null))).Json<CustomerDto>(HttpStatusCode.Created);

    // ---- authentication ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_NeedsNoKey()
    {
        using var anonymous = _host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/dashboard")]
    [InlineData("/api/admin/customers")]
    [InlineData("/api/admin/licenses")]
    [InlineData("/api/admin/modules")]
    [InlineData("/api/admin/packages")]
    [InlineData("/api/admin/backups")]
    [InlineData("/api/admin/audit")]
    [InlineData("/api/admin/installations")]
    public async Task EveryAdminEndpoint_RejectsRequestsWithoutAKey(string path)
    {
        using var anonymous = _host.CreateClient();

        var response = await anonymous.GetAsync(path);

        var error = await response.Error(HttpStatusCode.Unauthorized);
        Assert.Equal(CloudErrorCodes.Unauthorized, error.Code);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task WrongKey_AndWrongScheme_AreRejected()
    {
        using var wrong = _host.CreateClient().WithBearer("gpa_wrong");
        using var basic = _host.CreateClient();
        basic.DefaultRequestHeaders.Authorization = new("Basic", CloudWorld.AdminKey);

        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/admin/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await basic.GetAsync("/api/admin/dashboard")).StatusCode);
    }

    [Fact]
    public async Task WritesAreRejectedWithoutAKey_AndChangeNothing()
    {
        using var anonymous = _host.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/admin/customers", new CustomerRequest("Sneaky", null, null, null, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, (await _admin.GetFromJsonAsync<DashboardDto>("/api/admin/dashboard"))!.Customers);
    }

    [Fact]
    public async Task AdminResponses_AreNotCacheable()
    {
        var response = await _admin.GetAsync("/api/admin/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task InDevelopment_WithNoConfiguredKeys_AnEphemeralKeyIsLogged_AndWorks()
    {
        var logs = new CapturingLoggerProvider();
        var settings = _world.Settings.Where(kv => !kv.Key.StartsWith("AdminPortal:")).ToDictionary(kv => kv.Key, kv => kv.Value);
        using var host = ApiHosting.Host<AdminPortalApiMarker>(settings, logs: logs);
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/dashboard")).StatusCode);

        var key = Regex.Match(string.Join("\n", logs.Messages), "'(gpa_[^']+)'").Groups[1].Value;
        Assert.NotEmpty(key);
        Assert.Equal(HttpStatusCode.OK, (await client.WithBearer(key).GetAsync("/api/admin/dashboard")).StatusCode);
    }

    [Fact]
    public async Task OutsideDevelopment_WithNoConfiguredKeys_NoOneCanCallTheApi()
    {
        var settings = _world.Settings.Where(kv => !kv.Key.StartsWith("AdminPortal:")).ToDictionary(kv => kv.Key, kv => kv.Value);
        using var host = ApiHosting.Host<AdminPortalApiMarker>(settings, "Production");
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.WithBearer("gpa_anything").GetAsync("https://localhost/api/admin/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void MissingDatabaseConfiguration_FailsStartup_InsteadOfRunningWithoutPersistence()
    {
        var settings = new Dictionary<string, string?>(_world.Settings) { ["CloudDatabase:ConnectionString"] = "" };
        using var host = ApiHosting.Host<AdminPortalApiMarker>(settings);

        var ex = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("CloudDatabase:ConnectionString", ex.ToString());
    }

    // ---- customers ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Customers_CreateGetUpdateDeactivateList()
    {
        var response = await _admin.PostAsJsonAsync("/api/admin/customers", new CustomerRequest("Acme", "Pat", "pat@acme.test", null, null));
        var created = await response.Json<CustomerDto>(HttpStatusCode.Created);
        Assert.Equal($"/api/admin/customers/{created.Id}", response.Headers.Location!.OriginalString);

        Assert.Equal("Acme", (await _admin.GetFromJsonAsync<CustomerDto>($"/api/admin/customers/{created.Id}"))!.Name);

        var updated = await (await _admin.PutAsJsonAsync($"/api/admin/customers/{created.Id}", new CustomerRequest("Acme 2", null, null, null, null)))
            .Json<CustomerDto>(HttpStatusCode.OK);
        Assert.Equal("Acme 2", updated.Name);

        var inactive = await (await _admin.PostAsync($"/api/admin/customers/{created.Id}/deactivate", null)).Json<CustomerDto>(HttpStatusCode.OK);
        Assert.False(inactive.IsActive);
        Assert.Equal(0, (await _admin.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/admin/customers"))!.Total);
        Assert.Equal(1, (await _admin.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/admin/customers?includeInactive=true"))!.Total);

        var again = await _admin.PostAsync($"/api/admin/customers/{created.Id}/deactivate", null);
        Assert.Equal(CloudErrorCodes.InvalidState, (await again.Error(HttpStatusCode.Conflict)).Code);
        Assert.True((await (await _admin.PostAsync($"/api/admin/customers/{created.Id}/reactivate", null)).Json<CustomerDto>(HttpStatusCode.OK)).IsActive);
    }

    [Fact]
    public async Task Errors_MapToStatusCodes_AndCarryAStableErrorBody()
    {
        await CreateCustomer("Acme");

        var invalid = await _admin.PostAsJsonAsync("/api/admin/customers", new CustomerRequest("", null, null, null, null));
        var duplicate = await _admin.PostAsJsonAsync("/api/admin/customers", new CustomerRequest("acme", null, null, null, null));
        var missing = await _admin.GetAsync($"/api/admin/customers/{Guid.NewGuid()}");

        Assert.Equal(CloudErrorCodes.Validation, (await invalid.Error(HttpStatusCode.BadRequest)).Code);
        Assert.Equal(CloudErrorCodes.Conflict, (await duplicate.Error(HttpStatusCode.Conflict)).Code);
        Assert.Equal(CloudErrorCodes.NotFound, (await missing.Error(HttpStatusCode.NotFound)).Code);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync("/api/admin/customers/not-a-guid")).StatusCode);
    }

    [Fact]
    public async Task MalformedJson_IsABadRequest_NotAServerError()
    {
        var response = await _admin.PostAsync("/api/admin/customers", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- licenses ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Licenses_Create_ReturnsTheKeyOnce_AndLifecycleCallsWork()
    {
        var customer = await CreateCustomer();
        await _admin.PostAsJsonAsync("/api/admin/modules", new RegisterModuleRequest("pos", "POS", null, null));
        var from = DateTimeOffset.UtcNow;

        var created = await (await _admin.PostAsJsonAsync("/api/admin/licenses",
            new CreateLicenseRequest(customer.Id.ToString(), "genericpos", from, from.AddYears(1), ["pos"], ["advancedreports"])))
            .Json<CreateLicenseResponse>(HttpStatusCode.Created);

        Assert.NotEmpty(created.ActivationKey);
        var id = created.License.LicenseId;

        var fetched = await _admin.GetStringAsync($"/api/admin/licenses/{id}");
        Assert.DoesNotContain(created.ActivationKey, fetched);

        Assert.Equal("Suspended", (await (await _admin.PostAsJsonAsync($"/api/admin/licenses/{id}/suspend", new StatusChangeRequest("late")))
            .Json<LicenseDto>(HttpStatusCode.OK)).Status);
        Assert.Equal("Active", (await (await _admin.PostAsync($"/api/admin/licenses/{id}/reinstate", null)).Json<LicenseDto>(HttpStatusCode.OK)).Status);

        var extended = await (await _admin.PostAsJsonAsync($"/api/admin/licenses/{id}/extend", new ExtendLicenseRequest(from.AddYears(2))))
            .Json<LicenseDto>(HttpStatusCode.OK);
        Assert.Equal(from.AddYears(2), extended.ValidUntil);

        var entitled = await (await _admin.PutAsJsonAsync($"/api/admin/licenses/{id}/entitlements", new SetEntitlementsRequest(["pos"], ["x", "y"])))
            .Json<LicenseDto>(HttpStatusCode.OK);
        Assert.Equal(["x", "y"], entitled.Features);

        Assert.Equal("Revoked", (await (await _admin.PostAsync($"/api/admin/licenses/{id}/revoke", null)).Json<LicenseDto>(HttpStatusCode.OK)).Status);
        Assert.Equal(CloudErrorCodes.InvalidState, (await (await _admin.PostAsync($"/api/admin/licenses/{id}/suspend", null)).Error(HttpStatusCode.Conflict)).Code);

        var list = await _admin.GetFromJsonAsync<PagedResult<LicenseDto>>($"/api/admin/licenses?customerId={customer.Id}&status=revoked");
        Assert.Equal(id, Assert.Single(list!.Items).LicenseId);
    }

    [Fact]
    public async Task Licenses_ForUnknownCustomersOrModules_AreRejected()
    {
        var customer = await CreateCustomer();
        var from = DateTimeOffset.UtcNow;

        var ghostCustomer = await _admin.PostAsJsonAsync("/api/admin/licenses",
            new CreateLicenseRequest(Guid.NewGuid().ToString(), "p", from, from.AddDays(1), null, null));
        var ghostModule = await _admin.PostAsJsonAsync("/api/admin/licenses",
            new CreateLicenseRequest(customer.Id.ToString(), "p", from, from.AddDays(1), ["ghost"], null));

        Assert.Equal(CloudErrorCodes.NotFound, (await ghostCustomer.Error(HttpStatusCode.NotFound)).Code);
        Assert.Equal(CloudErrorCodes.Validation, (await ghostModule.Error(HttpStatusCode.BadRequest)).Code);
    }

    [Fact]
    public async Task BackupToken_CanBeIssuedAndRevoked_OverHttp()
    {
        var license = await _world.NewLicenseAsync();
        var id = license.License.LicenseId;

        var issued = await (await _admin.PostAsync($"/api/admin/licenses/{id}/backup-token", null)).Json<BackupTokenResponse>(HttpStatusCode.OK);
        Assert.StartsWith("gpb_", issued.Token);
        Assert.True((await _admin.GetFromJsonAsync<LicenseDto>($"/api/admin/licenses/{id}"))!.HasBackupAccess);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/admin/licenses/{id}/backup-token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"/api/admin/licenses/{id}/backup-token")).StatusCode);
    }

    // ---- registry & packages -----------------------------------------------------------------------------------

    [Fact]
    public async Task Modules_RegisterListRetire()
    {
        var created = await _admin.PostAsJsonAsync("/api/admin/modules", new RegisterModuleRequest("Accounting", "Accounting", "GL", "Optional"));
        var module = await created.Json<ModuleDto>(HttpStatusCode.Created);
        Assert.Equal("accounting", module.ModuleId);
        Assert.Equal("/api/admin/modules/accounting", created.Headers.Location!.OriginalString);

        Assert.Equal(CloudErrorCodes.Conflict, (await (await _admin.PostAsJsonAsync("/api/admin/modules",
            new RegisterModuleRequest("accounting", "Again", null, null))).Error(HttpStatusCode.Conflict)).Code);

        Assert.Single((await _admin.GetFromJsonAsync<List<ModuleDto>>("/api/admin/modules"))!);
        Assert.False((await (await _admin.PostAsync("/api/admin/modules/accounting/retire", null)).Json<ModuleDto>(HttpStatusCode.OK)).IsActive);
        Assert.Empty((await _admin.GetFromJsonAsync<List<ModuleDto>>("/api/admin/modules"))!);
        Assert.Single((await _admin.GetFromJsonAsync<List<ModuleDto>>("/api/admin/modules?includeRetired=true"))!);
        Assert.Equal("accounting", (await _admin.GetFromJsonAsync<ModuleDetailDto>("/api/admin/modules/accounting"))!.Module.ModuleId);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync("/api/admin/modules/ghost")).StatusCode);
    }

    [Fact]
    public async Task Packages_Upload_List_Withdraw_Restore()
    {
        await _world.NewModuleAsync("catalog");
        var path = _world.MakePackage("catalog", "1.0.0");

        using var upload = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        upload.Headers.ContentType = new("application/octet-stream");
        var response = await _admin.PostAsync("/api/admin/packages?releaseNotes=first%20release", upload);
        var dto = await response.Json<PackageDto>(HttpStatusCode.Created);

        Assert.Equal("first release", dto.ReleaseNotes);
        Assert.Equal("tester", dto.PublishedBy);
        Assert.Equal($"/api/admin/packages/{dto.PackageId}", response.Headers.Location!.OriginalString);
        Assert.Single((await _admin.GetFromJsonAsync<List<PackageDto>>("/api/admin/packages?targetId=catalog&status=published"))!);

        Assert.Equal("Withdrawn", (await (await _admin.PostAsJsonAsync($"/api/admin/packages/{dto.PackageId}/withdraw", new StatusChangeRequest("bug")))
            .Json<PackageDto>(HttpStatusCode.OK)).Status);
        Assert.Empty((await _admin.GetFromJsonAsync<List<PackageDto>>("/api/admin/packages?status=published"))!);
        Assert.Equal("Published", (await (await _admin.PostAsync($"/api/admin/packages/{dto.PackageId}/restore", null)).Json<PackageDto>(HttpStatusCode.OK)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/admin/packages/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Packages_InvalidUploads_AreBadRequests()
    {
        using var garbage = new ByteArrayContent("not a package"u8.ToArray());
        var bad = await _admin.PostAsync("/api/admin/packages", garbage);

        using var unregistered = new ByteArrayContent(await File.ReadAllBytesAsync(_world.MakePackage("ghost", "1.0.0")));
        var ghost = await _admin.PostAsync("/api/admin/packages", unregistered);

        Assert.Equal(CloudErrorCodes.InvalidPackage, (await bad.Error(HttpStatusCode.BadRequest)).Code);
        Assert.Equal(CloudErrorCodes.Validation, (await ghost.Error(HttpStatusCode.BadRequest)).Code);
        Assert.Empty((await _admin.GetFromJsonAsync<List<PackageDto>>("/api/admin/packages"))!);
    }

    // ---- operations --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dashboard_Installations_Backups_AndAudit_AreAvailable()
    {
        var customer = await CreateCustomer("Acme");
        var audit = await _admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>("/api/admin/audit?entityType=customer");
        var entry = Assert.Single(audit!.Items);
        Assert.Equal(("customer.create", "tester", customer.Id.ToString()), (entry.Action, entry.Actor, entry.EntityId));

        var dashboard = await _admin.GetFromJsonAsync<DashboardDto>("/api/admin/dashboard");
        Assert.Equal(1, dashboard!.Customers);
        Assert.Empty((await _admin.GetFromJsonAsync<PagedResult<InstallationDto>>("/api/admin/installations"))!.Items);
        Assert.Empty((await _admin.GetFromJsonAsync<PagedResult<Cloud.Contracts.Backup.BackupDto>>("/api/admin/backups"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync($"/api/admin/backups/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Audit_CanBeFilteredByActor_AndRejectsNoWrites()
    {
        await CreateCustomer("Acme");

        var byActor = await _admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>("/api/admin/audit?actor=tester");
        var byOther = await _admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>("/api/admin/audit?actor=somebody-else");

        Assert.Equal(1, byActor!.Total);
        Assert.Equal(0, byOther!.Total);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _admin.DeleteAsync("/api/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _admin.PutAsJsonAsync("/api/admin/audit", new { })).StatusCode);
    }
}
