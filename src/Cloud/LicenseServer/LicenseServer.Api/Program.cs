using Cloud.Contracts;
using Cloud.Hosting;
using Cloud.Infrastructure;
using LicenseServer.Application;
using LicenseServer.Infrastructure;
using Licensing.Contracts;
using Microsoft.AspNetCore.Http.Json;

// Minimal license server (Stage 6 foundation).
//   POST /api/licenses/activate   body: ActivationRequest  -> ActivationResponse (signed license)
//   POST /api/licenses/renew      body: RenewalRequest     -> RenewalResponse   (signed license/lease)
//
// Durable storage: with CloudDatabase:ConnectionString configured (Stage 9) licenses live in the shared server database and are
// administered through AdminPortal.Api; otherwise the in-memory store is used (Development only).
// NOT included: customer-facing authentication beyond the activation key, billing, multi-tenancy.
//
// Signing key handling:
//   LicenseServer:SigningKeyPemPath  PKCS#8 PEM private key file kept OUTSIDE the repository (required in production)
//   LicenseServer:KeyId              identifier carried in licenses (default "license-key-1")
//   In the Development environment ONLY, a missing key path generates an EPHEMERAL key and logs its PUBLIC key.
var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = LicenseSerializer.Options.PropertyNamingPolicy;
    foreach (var c in LicenseSerializer.Options.Converters) o.SerializerOptions.Converters.Add(c);
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddCloudSecurity(builder.Configuration);

// License storage. With CloudDatabase:ConnectionString configured, licenses live in the durable server database shared with
// the administration host (activation keys stored as hashes). Without it, the in-memory store is used, which is allowed in
// Development ONLY because everything in it is lost on restart.
if (!string.IsNullOrWhiteSpace(builder.Configuration["CloudDatabase:ConnectionString"]))
{
    builder.Services.AddCloudDatabase(builder.Configuration);
    builder.Services.AddLicenseStore();
}
else if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<ILicenseRepository, InMemoryLicenseRepository>();
}
else
{
    throw new InvalidOperationException(
        "CloudDatabase:ConnectionString is required outside the Development environment (the in-memory license store is not durable).");
}

builder.Services.AddSingleton(sp => new LicenseServerOptions(
    LeaseDuration: TimeSpan.FromDays(builder.Configuration.GetValue("LicenseServer:LeaseDays", 30)),
    GraceDuration: TimeSpan.FromDays(builder.Configuration.GetValue("LicenseServer:GraceDays", 7)),
    Issuer: builder.Configuration.GetValue("LicenseServer:Issuer", "GenericPOS License Server")!));

builder.Services.AddSingleton<ILicenseSigner>(sp =>
{
    var keyId = builder.Configuration.GetValue("LicenseServer:KeyId", "license-key-1")!;
    var path = builder.Configuration["LicenseServer:SigningKeyPemPath"];

    if (!string.IsNullOrWhiteSpace(path))
        return EcdsaLicenseSigner.FromPemFile(path, keyId);

    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException(
            "LicenseServer:SigningKeyPemPath is required outside the Development environment.");

    var dev = EcdsaLicenseSigner.GenerateEphemeral("dev-ephemeral");
    sp.GetRequiredService<ILogger<Program>>().LogWarning(
        "DEVELOPMENT ONLY: using an ephemeral signing key. Trust it on a client with KeyId '{KeyId}' and public key: {PublicKey}",
        dev.KeyId, dev.ExportPublicKey());
    return dev;
});

builder.Services.AddSingleton<LicenseIssuanceService>();

var app = builder.Build();

app.UseCloudSecurityHeaders();

app.UseCloudTransportSecurity();   // HSTS, HTTPS redirection and a hard refusal of plain HTTP outside Development

// A probe for load balancers and monitors: reachable over plain HTTP behind a proxy, reveals nothing.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// DEVELOPMENT ONLY: provision licenses from configuration (LicenseServer:DevLicenses) so the API can be exercised
// before a real license administration exists. Never runs outside the Development environment.
if (app.Environment.IsDevelopment())
{
    var repository = app.Services.GetRequiredService<ILicenseRepository>();
    foreach (var dev in app.Configuration.GetSection("LicenseServer:DevLicenses").Get<List<DevLicense>>() ?? [])
    {
        if (await repository.FindByActivationKeyAsync(dev.ActivationKey) is not null)
            continue; // a durable store keeps the license from the previous run

        await repository.AddAsync(new LicenseRecord
        {
            LicenseId = Guid.NewGuid(),
            CustomerId = dev.CustomerId,
            ActivationKey = dev.ActivationKey,
            ProductId = dev.ProductId,
            ValidFrom = dev.ValidFrom,
            ValidUntil = dev.ValidUntil,
            Modules = dev.Modules,
            Features = dev.Features
        });
    }
}

// An unknown activation key (or license/installation pair) is a failed credential: a caller that keeps guessing is turned away for a while.
app.MapPost("/api/licenses/activate", async (HttpContext http, ActivationRequest request, LicenseIssuanceService service,
    AuthenticationThrottle throttle, ICloudSecurityLog securityLog, CancellationToken ct) =>
{
    var caller = http.CallerKey();
    var decision = throttle.Check(caller);
    if (!decision.Allowed)
        return http.TooManyRequests(decision);

    var response = await service.ActivateAsync(request, ct);
    if (response.IsSuccess)
        throttle.RecordSuccess(caller);
    else if (response.ErrorCode == LicenseErrorCodes.NotFound)
        await RecordGuessAsync(throttle, securityLog, caller, "license.activation-failed", "An unknown activation key was presented.");

    return Results.Json(response, LicenseSerializer.Options, statusCode: response.IsSuccess ? 200 : 400);
});

app.MapPost("/api/licenses/renew", async (HttpContext http, RenewalRequest request, LicenseIssuanceService service,
    AuthenticationThrottle throttle, ICloudSecurityLog securityLog, CancellationToken ct) =>
{
    var caller = http.CallerKey();
    var decision = throttle.Check(caller);
    if (!decision.Allowed)
        return http.TooManyRequests(decision);

    var response = await service.RenewAsync(request, ct);
    if (response.IsSuccess)
        throttle.RecordSuccess(caller);
    else if (response.ErrorCode is LicenseErrorCodes.NotFound or LicenseErrorCodes.InstallationMismatch)
        await RecordGuessAsync(throttle, securityLog, caller, "license.renewal-failed", "A renewal named an unknown license or the wrong installation.");

    return Results.Json(response, LicenseSerializer.Options, statusCode: response.IsSuccess ? 200 : 400);
});

static async Task RecordGuessAsync(AuthenticationThrottle throttle, ICloudSecurityLog securityLog, string caller, string action, string summary)
{
    var blocked = throttle.RecordFailure(caller);
    await securityLog.RecordAsync("anonymous", blocked ? "license.auth-blocked" : action, "license-access", caller,
        blocked ? "Too many failed license requests; the caller was blocked for a while." : summary);
}

app.Run();

/// <summary>Shape of a development-seeded license in configuration.</summary>
public sealed record DevLicense(
    string ActivationKey,
    string CustomerId,
    string ProductId,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    List<string> Modules,
    List<string> Features);

/// <summary>Exposed so integration tests can host the API in-process.</summary>
public partial class Program;
