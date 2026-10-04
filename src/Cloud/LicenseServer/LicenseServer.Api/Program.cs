using LicenseServer.Application;
using LicenseServer.Infrastructure;
using Licensing.Contracts;
using Microsoft.AspNetCore.Http.Json;

// Minimal license server (Stage 6 foundation).
//   POST /api/licenses/activate   body: ActivationRequest  -> ActivationResponse (signed license)
//   POST /api/licenses/renew      body: RenewalRequest     -> RenewalResponse   (signed license/lease)
//
// NOT included (later stages): customer authentication, admin portal, billing, durable storage.
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
builder.Services.AddSingleton<ILicenseRepository, InMemoryLicenseRepository>();
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

// DEVELOPMENT ONLY: provision licenses from configuration (LicenseServer:DevLicenses) so the API can be exercised
// before a real license administration exists. Never runs outside the Development environment.
if (app.Environment.IsDevelopment())
{
    var repository = app.Services.GetRequiredService<ILicenseRepository>();
    foreach (var dev in app.Configuration.GetSection("LicenseServer:DevLicenses").Get<List<DevLicense>>() ?? [])
    {
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

app.MapPost("/api/licenses/activate", async (ActivationRequest request, LicenseIssuanceService service, CancellationToken ct) =>
{
    var response = await service.ActivateAsync(request, ct);
    return Results.Json(response, LicenseSerializer.Options, statusCode: response.IsSuccess ? 200 : 400);
});

app.MapPost("/api/licenses/renew", async (RenewalRequest request, LicenseIssuanceService service, CancellationToken ct) =>
{
    var response = await service.RenewAsync(request, ct);
    return Results.Json(response, LicenseSerializer.Options, statusCode: response.IsSuccess ? 200 : 400);
});

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
