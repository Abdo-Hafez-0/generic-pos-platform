using Updates.Contracts;
using UpdateServer.Application;

// Minimal update server (Stage 7 foundation).
//   POST /api/updates/check                 body: UpdateCheckRequest  -> UpdateCheckResponse (signed manifests, never forced)
//   GET  /api/updates/packages/{packageId}  -> the .gpkg file
//
// NOT included (later stages): customer authentication, admin portal, publishing API, billing, CDN, multi-tenancy.
// The server holds NO signing keys: packages are signed by UpdatePublisher and verified by clients.
// Production transport is HTTPS (terminated by the host/reverse proxy).
//   UpdateServer:PackageDirectory   directory of .gpkg files to serve
var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = PackageManifestSerializer.Options.PropertyNamingPolicy;
    foreach (var c in PackageManifestSerializer.Options.Converters) o.SerializerOptions.Converters.Add(c);
});

builder.Services.AddSingleton<IPackageRepository>(_ =>
    new DirectoryPackageRepository(builder.Configuration["UpdateServer:PackageDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "packages")));
builder.Services.AddSingleton<UpdateDiscoveryService>();

var app = builder.Build();

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
