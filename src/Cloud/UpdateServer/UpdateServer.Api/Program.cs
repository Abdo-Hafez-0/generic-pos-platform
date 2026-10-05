using Cloud.Infrastructure;
using Updates.Contracts;
using UpdateServer.Application;

// Minimal update server (Stage 7 foundation).
//   POST /api/updates/check                 body: UpdateCheckRequest  -> UpdateCheckResponse (signed manifests, never forced)
//   GET  /api/updates/packages/{packageId}  -> the .gpkg file
//
// Publishing/withdrawing packages is done through AdminPortal.Api (Stage 9) when CloudDatabase:ConnectionString is configured.
// NOT included: customer authentication, billing, CDN, multi-tenancy.
// The server holds NO signing keys: packages are signed by UpdatePublisher and verified by clients.
// Production transport is HTTPS (terminated by the host/reverse proxy).
//   UpdateServer:PackageDirectory   directory of .gpkg files to serve
var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = PackageManifestSerializer.Options.PropertyNamingPolicy;
    foreach (var c in PackageManifestSerializer.Options.Converters) o.SerializerOptions.Converters.Add(c);
});

// Package catalog. With CloudDatabase:ConnectionString configured, packages are the ones published through the administration
// host (durable catalog; withdrawn packages are neither offered nor downloadable) and UpdateServer:PackageDirectory is where
// their bytes live. Without it, the Stage 7 behavior is unchanged: serve every valid .gpkg in the directory.
if (!string.IsNullOrWhiteSpace(builder.Configuration["CloudDatabase:ConnectionString"]))
{
    builder.Services.AddCloudDatabase(builder.Configuration);
    builder.Services.AddPackageStore(builder.Configuration);
}
else
{
    builder.Services.AddSingleton<IPackageRepository>(_ =>
        new DirectoryPackageRepository(builder.Configuration["UpdateServer:PackageDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "packages")));
}

builder.Services.AddSingleton<UpdateDiscoveryService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.MapPost("/api/updates/check", (UpdateCheckRequest request, UpdateDiscoveryService discovery) =>
{
    var response = discovery.Check(request);
    return Results.Json(response, PackageManifestSerializer.Options, statusCode: response.IsSuccess ? 200 : 400);
});

app.MapGet("/api/updates/packages/{packageId:guid}", (Guid packageId, IPackageRepository repository) =>
{
    var stream = repository.OpenRead(packageId);
    return stream is null
        ? Results.NotFound()
        : Results.File(stream, "application/octet-stream", $"{packageId:N}{Updates.Package.PackageFormat.Extension}");
});

app.Run();

/// <summary>Exposed so integration tests can host the API in-process.</summary>
public partial class Program;
