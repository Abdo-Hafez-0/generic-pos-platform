extern alias LicenseApi;
extern alias UpdateApi;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AdminPortal.Application;
using Cloud.Contracts;
using Licensing.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cloud.Tests;

public sealed class AuthenticationThrottleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static (AuthenticationThrottle Throttle, Clock Time) Build(int max = 3, int windowMinutes = 10, int lockoutMinutes = 15)
    {
        var time = new Clock(T0);
        return (new AuthenticationThrottle(new AuthenticationThrottleOptions(max, TimeSpan.FromMinutes(windowMinutes), TimeSpan.FromMinutes(lockoutMinutes)), time), time);
    }

    [Fact]
    public void A_caller_is_blocked_on_the_failure_that_reaches_the_limit_and_then_refused_even_with_a_right_credential()
    {
        var (throttle, _) = Build();

        Assert.False(throttle.RecordFailure("1.2.3.4"));
        Assert.False(throttle.RecordFailure("1.2.3.4"));
        Assert.True(throttle.Check("1.2.3.4").Allowed);
        Assert.True(throttle.RecordFailure("1.2.3.4"));

        var decision = throttle.Check("1.2.3.4");
        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(15), decision.RetryAfter);
    }

    [Fact]
    public void One_callers_failures_never_block_another()
    {
        var (throttle, _) = Build();
        for (var i = 0; i < 5; i++) throttle.RecordFailure("attacker");

        Assert.False(throttle.Check("attacker").Allowed);
        Assert.True(throttle.Check("customer").Allowed);
    }

    [Fact]
    public void The_block_ends_after_the_lockout_and_the_count_starts_again()
    {
        var (throttle, time) = Build();
        for (var i = 0; i < 3; i++) throttle.RecordFailure("c");

        time.Advance(TimeSpan.FromMinutes(14));
        Assert.False(throttle.Check("c").Allowed);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(throttle.Check("c").Allowed);

        Assert.False(throttle.RecordFailure("c"));   // a fresh count: one failure is not an immediate re-block
    }

    [Fact]
    public void Old_failures_stop_counting_after_the_window()
    {
        var (throttle, time) = Build();
        throttle.RecordFailure("c");
        throttle.RecordFailure("c");

        time.Advance(TimeSpan.FromMinutes(11));

        Assert.False(throttle.RecordFailure("c"));
        Assert.True(throttle.Check("c").Allowed);
    }

    [Fact]
    public void A_success_forgets_the_failures()
    {
        var (throttle, _) = Build();
        throttle.RecordFailure("c");
        throttle.RecordFailure("c");

        throttle.RecordSuccess("c");

        Assert.False(throttle.RecordFailure("c"));
        Assert.False(throttle.RecordFailure("c"));
        Assert.True(throttle.Check("c").Allowed);
    }

    [Fact]
    public void Configuration_can_tighten_but_never_disable_the_throttle()
    {
        var weak = new AuthenticationThrottleOptions(1, TimeSpan.Zero, TimeSpan.Zero).Normalized();

        Assert.Equal(AuthenticationThrottleOptions.MaxFailuresFloor, weak.MaxFailures);
        Assert.Equal(AuthenticationThrottleOptions.DefaultWindow, weak.EffectiveWindow);
        Assert.Equal(AuthenticationThrottleOptions.DefaultLockout, weak.EffectiveLockout);
    }

    [Fact]
    public void The_number_of_tracked_callers_is_bounded()
    {
        var (throttle, _) = Build();

        for (var i = 0; i < 25_000; i++) throttle.RecordFailure("ip-" + i);

        // Reaching here without exhausting memory is the point; and a fresh caller is still handled normally.
        Assert.True(throttle.Check("someone-new").Allowed);
        Assert.False(throttle.RecordFailure("someone-new"));
    }
}

/// <summary>The server hosts, driven only through HTTP: throttling, the security log, headers, HTTPS and fail-closed start-up.</summary>
public sealed class CloudSecurityTests : IDisposable
{
    private readonly CloudWorld _world = new();
    private readonly ECDsa _licenseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Dictionary<string, string?> _settings;
    private readonly List<IDisposable> _disposables = [];

    public CloudSecurityTests()
    {
        var pem = Path.Combine(_world.Dir, "license-signing-key.pem");
        File.WriteAllText(pem, _licenseKey.ExportPkcs8PrivateKeyPem());
        _settings = new Dictionary<string, string?>(_world.Settings)
        {
            ["LicenseServer:SigningKeyPemPath"] = pem,
            ["LicenseServer:KeyId"] = "sec-key",
            ["Security:AuthThrottle:MaxFailures"] = "3"
        };
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_disposables)) d.Dispose();
        _licenseKey.Dispose();
        _world.Dispose();
    }

    private WebApplicationFactory<T> Host<T>(string environment = "Development", Dictionary<string, string?>? settings = null) where T : class
    {
        var host = ApiHosting.Host<T>(settings ?? _settings, environment, clock: _world.Clock);
        _disposables.Add(host);
        return host;
    }

    private HttpClient Client<T>(string? token = null, string environment = "Development") where T : class
    {
        var client = Host<T>(environment).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).WithBearer(token);
        _disposables.Add(client);
        return client;
    }

    private async Task<IReadOnlyList<AdminAuditEntry>> AuditAsync(string? actorPrefix = null)
    {
        var page = await _world.Get<IAdminAuditLog>().QueryAsync(new AuditFilter(), 1, 200);
        return page.Items.Where(e => actorPrefix is null || e.Actor.StartsWith(actorPrefix, StringComparison.Ordinal)).ToList();
    }

    // ------------------------------------------------------------------ throttling and the security log, per host

    [Fact]
    public async Task The_backup_host_turns_away_a_caller_that_keeps_presenting_wrong_tokens_even_when_the_next_token_is_right()
    {
        var license = await _world.NewLicenseAsync(["cloud-backup"]);
        await _world.ActivateAsync(license);
        var token = ResultAssert.Ok(await _world.Licenses.IssueBackupTokenAsync(_world.Actor, license.License.LicenseId)).Token;

        // one host (one throttle) serves both the wrong-token caller and the right-token caller: they share the caller key in-process
        var host = Host<BackupServerApiMarker>();
        using var bad = host.CreateClient().WithBearer("gpb_wrong-token-here");
        using var good = host.CreateClient().WithBearer(token);

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync("/api/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await good.GetAsync("/api/backups")).StatusCode); // a success from this caller forgets the failures
        for (var i = 0; i < 3; i++) await bad.GetAsync("/api/backups");

        var blocked = await good.GetAsync("/api/backups");   // the right token, but this caller is now blocked
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(CloudErrorCodes.TooManyRequests, (await blocked.Content.ReadFromJsonAsync<ApiError>())!.Code);
        Assert.True(int.Parse(blocked.Headers.GetValues("Retry-After").Single()) > 0);
    }

    [Fact]
    public async Task Failed_backup_authentications_and_the_block_are_recorded_without_the_token()
    {
        var host = Host<BackupServerApiMarker>();
        using var bad = host.CreateClient().WithBearer("gpb_SuperSecretWrongToken1234567890");

        for (var i = 0; i < 4; i++) await bad.GetAsync("/api/backups");

        var entries = await AuditAsync("anonymous");
        Assert.Contains(entries, e => e.Action == "backup.auth-failed");
        Assert.Contains(entries, e => e.Action == "backup.auth-blocked");
        Assert.DoesNotContain(entries, e => (e.Summary + e.EntityId).Contains("SuperSecretWrongToken"));
    }

    [Fact]
    public async Task The_admin_host_throttles_wrong_keys_and_logs_them()
    {
        var host = Host<AdminPortalApiMarker>();
        using var bad = host.CreateClient().WithBearer("gpa_wrong-admin-key-123456");
        using var good = host.CreateClient().WithBearer(CloudWorld.AdminKey);

        for (var i = 0; i < 3; i++) await bad.GetAsync("/api/admin/dashboard");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await good.GetAsync("/api/admin/dashboard")).StatusCode);
        var entries = await AuditAsync("anonymous");
        Assert.Contains(entries, e => e.Action == "admin.auth-failed");
        Assert.Contains(entries, e => e.Action == "admin.auth-blocked");
        Assert.DoesNotContain(entries, e => e.Summary.Contains("gpa_"));
    }

    [Fact]
    public async Task The_license_host_throttles_guessing_of_activation_keys_and_a_success_resets_the_count()
    {
        var license = await _world.NewLicenseAsync(["cloud-backup"]);
        var host = Host<LicenseApi::Program>();
        using var client = host.CreateClient();
        var installation = Guid.NewGuid();

        async Task<HttpResponseMessage> Activate(string key)
            => await client.PostAsJsonAsync("/api/licenses/activate", new ActivationRequest(key, installation, "genericpos"), LicenseSerializer.Options);

        Assert.Equal(HttpStatusCode.BadRequest, (await Activate("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Activate("AAAAA-BBBBB-CCCCC-DDDDD-FFFFF")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Activate(license.ActivationKey)).StatusCode);            // the right key: resets the count
        for (var i = 0; i < 3; i++) await Activate("GGGGG-HHHHH-JJJJJ-KKKKK-LLLLL");

        var blocked = await Activate(license.ActivationKey);                                              // right key, blocked caller
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        var entries = await AuditAsync("anonymous");
        Assert.Contains(entries, e => e.Action == "license.auth-blocked");
        Assert.DoesNotContain(entries, e => e.Summary.Contains("AAAAA-BBBBB") || e.Summary.Contains(license.ActivationKey));
    }

    [Fact]
    public async Task Customer_backup_activity_is_recorded_under_the_license_and_never_with_content_or_token()
    {
        var license = await _world.NewLicenseAsync(["cloud-backup"]);
        await _world.ActivateAsync(license);
        var token = ResultAssert.Ok(await _world.Licenses.IssueBackupTokenAsync(_world.Actor, license.License.LicenseId)).Token;
        using var client = Host<BackupServerApiMarker>().CreateClient().WithBearer(token);
        var bytes = RandomNumberGenerator.GetBytes(2048);

        var created = await client.PostAsync("/api/backups", new ByteArrayContent(bytes));
        var dto = (await created.Content.ReadFromJsonAsync<Cloud.Contracts.Backup.BackupDto>())!;
        await client.GetAsync($"/api/backups/{dto.BackupId}/content");
        await client.DeleteAsync($"/api/backups/{dto.BackupId}");

        var entries = await AuditAsync($"license:{license.License.LicenseId}");
        Assert.Equal(["backup.delete", "backup.download", "backup.upload"], entries.Select(e => e.Action).OrderBy(a => a, StringComparer.Ordinal).ToArray());
        Assert.All(entries, e => Assert.Equal(dto.BackupId.ToString(), e.EntityId));
        var everything = string.Join(" ", entries.Select(e => e.Summary));
        Assert.DoesNotContain(token, everything);
        Assert.DoesNotContain(Convert.ToBase64String(bytes[..32]), everything);
    }

    [Fact]
    public async Task A_refused_upload_is_recorded_too()
    {
        var license = await _world.NewLicenseAsync([]);                         // no cloud-backup entitlement
        await _world.ActivateAsync(license);
        var token = ResultAssert.Ok(await _world.Licenses.IssueBackupTokenAsync(_world.Actor, license.License.LicenseId)).Token;
        using var client = Host<BackupServerApiMarker>().CreateClient().WithBearer(token);

        var refused = await client.PostAsync("/api/backups", new ByteArrayContent([1, 2, 3]));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(await AuditAsync($"license:{license.License.LicenseId}"), e => e.Action == "backup.upload-denied");
    }

    // ------------------------------------------------------------------ headers and transport

    [Fact]
    public async Task Every_host_sends_the_security_headers_and_keeps_api_answers_out_of_caches()
    {
        foreach (var (name, client, path) in new (string, HttpClient, string)[]
        {
            ("admin", Client<AdminPortalApiMarker>(CloudWorld.AdminKey), "/api/admin/dashboard"),
            ("backup", Client<BackupServerApiMarker>("gpb_x"), "/api/backups"),
            ("license", Client<LicenseApi::Program>(), "/api/licenses/renew"),
            ("update", Client<UpdateApi::Program>(), "/api/updates/packages/" + Guid.NewGuid())
        })
        {
            var response = await client.GetAsync(path);
            Assert.True(response.Headers.Contains("X-Content-Type-Options"), name);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString(), ignoreCase: true);
        }
    }

    private Dictionary<string, string?> ProductionSettings()
    {
        var settings = new Dictionary<string, string?>(_settings)
        {
            ["UpdateServer:TrustedKeys:0:KeyId"] = CloudWorld.PublisherKeyId,
            ["UpdateServer:TrustedKeys:0:PublicKey"] = _world.PublisherKey.ExportPublicKey()
        };
        return settings;
    }

    private IEnumerable<(string Name, Func<Dictionary<string, string?>, HttpClient> Create, string ApiPath)> AllHosts() =>
    [
        ("admin", s => ApiHosting.Host<AdminPortalApiMarker>(s, "Production").CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), "/api/admin/dashboard"),
        ("backup", s => ApiHosting.Host<BackupServerApiMarker>(s, "Production").CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), "/api/backups"),
        ("license", s => ApiHosting.Host<LicenseApi::Program>(s, "Production").CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), "/api/licenses/renew"),
        ("update", s => ApiHosting.Host<UpdateApi::Program>(s, "Production").CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }), "/api/updates/packages/" + Guid.NewGuid())
    ];

    [Fact]
    public async Task Outside_Development_no_host_ever_serves_an_API_request_over_plain_http()
    {
        foreach (var (name, create, path) in AllHosts())
        {
            using var client = create(ProductionSettings());

            var plain = await client.GetAsync("http://api.example.test" + path);

            Assert.Equal(HttpStatusCode.Forbidden, plain.StatusCode);
            Assert.Equal(CloudErrorCodes.HttpsRequired, (await plain.Content.ReadFromJsonAsync<ApiError>())!.Code);
            Assert.NotEqual(HttpStatusCode.Unauthorized, plain.StatusCode);   // it was refused before any credential check
            _ = name;
        }
    }

    [Fact]
    public async Task Outside_Development_https_requests_are_served_with_hsts_and_health_probes_stay_reachable()
    {
        foreach (var (name, create, _) in AllHosts())
        {
            using var client = create(ProductionSettings());

            var secure = await client.GetAsync("https://api.example.test/health");
            var probe = await client.GetAsync("http://api.example.test/health");

            Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
            Assert.True(secure.Headers.Contains("Strict-Transport-Security"), $"{name} sends HSTS over https");
            Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        }
    }

    [Fact]
    public async Task Where_the_https_port_is_known_plain_http_is_redirected_instead()
    {
        var settings = ProductionSettings();
        settings["https_port"] = "443";
        using var client = ApiHosting.Host<BackupServerApiMarker>(settings, "Production").CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var plain = await client.GetAsync("http://api.example.test/api/backups");

        Assert.True(plain.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect);
        Assert.StartsWith("https://", plain.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Development_does_not_redirect_so_local_testing_works_without_certificates()
    {
        using var client = Client<BackupServerApiMarker>();

        var response = await client.GetAsync("http://localhost/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------ fail-closed start-up

    [Fact]
    public void Outside_Development_the_admin_portal_refuses_to_start_without_trusted_package_keys()
    {
        var host = ApiHosting.Host<AdminPortalApiMarker>(_settings, "Production");
        _disposables.Add(host);

        var ex = Assert.ThrowsAny<Exception>(() => host.CreateClient());

        Assert.Contains("UpdateServer:TrustedKeys", ex.ToString());
    }

    [Fact]
    public async Task With_trusted_package_keys_the_admin_portal_starts_in_production()
    {
        var settings = new Dictionary<string, string?>(_settings)
        {
            ["UpdateServer:TrustedKeys:0:KeyId"] = CloudWorld.PublisherKeyId,
            ["UpdateServer:TrustedKeys:0:PublicKey"] = _world.PublisherKey.ExportPublicKey()
        };
        using var client = ApiHosting.Host<AdminPortalApiMarker>(settings, "Production")
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).WithBearer(CloudWorld.AdminKey);

        var response = await client.GetAsync("https://localhost/api/admin/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
