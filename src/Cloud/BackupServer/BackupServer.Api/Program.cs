using BackupServer.Application;
using Cloud.Contracts;
using Cloud.Contracts.Backup;
using Cloud.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

// Cloud backup API (Stage 9): the SERVER side of cloud backup. A client presents the backup access token the vendor issued for
// its license (Authorization: Bearer <token>) and uploads/lists/downloads/deletes OPAQUE backup bytes. The server never opens,
// decrypts or restores a backup, and the desktop POS never needs this service to operate (cloud backup stays optional).
//
//   POST   /api/backups                  body = backup bytes; optional header X-Backup-Sha256, X-Client-Version; query ?label=
//   GET    /api/backups                  the caller's backups, newest first
//   GET    /api/backups/{id}             metadata
//   GET    /api/backups/{id}/content     the bytes (X-Backup-Sha256 response header lets the client verify)
//   DELETE /api/backups/{id}
//
//   Configuration: CloudDatabase:ConnectionString, BackupServer:StorageDirectory, BackupServer:MaxBackupBytes,
//   BackupServer:MaxBackupsPerLicense, BackupServer:RequiredModule (default "cloud-backup").
//
// Production transport is HTTPS (HSTS + redirection are enabled outside Development).
// NOT included (later stages): backup encryption/key management and stronger client authentication (Stage 11), client-side
// backup scheduling (the optional CloudBackup module), synchronization.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCloudDatabase(builder.Configuration);
builder.Services.AddBackupServices(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var backups = app.MapGroup("/api/backups").AddEndpointFilter<BackupAuthenticationFilter>();

backups.MapPost("/", async (HttpContext http, BackupService service, BackupServerOptions options, string? label, CancellationToken ct) =>
{
    var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = options.MaxBackupBytes;

    var info = new BackupUploadInfo(
        http.Request.Headers[BackupHeaders.Sha256].ToString(), label, http.Request.Headers[BackupHeaders.ClientVersion].ToString());

    var r = await service.UploadAsync(http.Principal(), http.Request.Body, info, ct);
    return r.IsSuccess ? Results.Created($"/api/backups/{r.Value!.BackupId}", ToDto(r.Value)) : r.Fail();
});

backups.MapGet("/", async (HttpContext http, BackupService service, CancellationToken ct)
    => Results.Ok((await service.ListAsync(http.Principal(), ct)).Select(ToDto).ToList()));

backups.MapGet("/{id:guid}", async (HttpContext http, Guid id, BackupService service, CancellationToken ct) =>
{
    var r = await service.FindAsync(http.Principal(), id, ct);
    return r.IsSuccess ? Results.Ok(ToDto(r.Value!)) : r.Fail();
});

backups.MapGet("/{id:guid}/content", async (HttpContext http, Guid id, BackupService service, CancellationToken ct) =>
{
    var found = await service.FindAsync(http.Principal(), id, ct);
    if (!found.IsSuccess) return found.Fail();

    var opened = await service.OpenContentAsync(http.Principal(), id, ct);
    if (!opened.IsSuccess) return opened.Fail();

    http.Response.Headers[BackupHeaders.Sha256] = found.Value!.Sha256;
    return Results.Stream(opened.Value!, "application/octet-stream", $"{id:N}.bak");
});

backups.MapDelete("/{id:guid}", async (HttpContext http, Guid id, BackupService service, CancellationToken ct) =>
{
    var r = await service.DeleteAsync(http.Principal(), id, ct);
    return r.IsSuccess ? Results.NoContent() : r.Fail();
});

app.Run();

static BackupDto ToDto(BackupRecord r)
    => new(r.BackupId, r.LicenseId, r.CustomerId, r.InstallationId, r.CreatedAt, r.SizeBytes, r.Sha256, r.Label, r.ClientVersion);

/// <summary>Authenticates backup requests by the license's backup access token and keeps responses out of caches.</summary>
internal sealed class BackupAuthenticationFilter(BackupAccessService access) : IEndpointFilter
{
    public const string PrincipalKey = "backup.principal";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";

        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..] : null;

        var result = await access.AuthenticateAsync(token, http.RequestAborted);
        if (!result.IsSuccess)
        {
            if (result.Error!.Code == CloudErrorCodes.Unauthorized)
                http.Response.Headers.WWWAuthenticate = "Bearer";

            return result.Fail();
        }

        http.Items[PrincipalKey] = result.Value!;
        return await next(context);
    }
}

internal static class BackupHttpExtensions
{
    public static BackupPrincipal Principal(this HttpContext http) => (BackupPrincipal)http.Items[BackupAuthenticationFilter.PrincipalKey]!;

    public static IResult Fail(this ServiceResult result)
        => Results.Json(result.Error!, statusCode: CloudErrorCodes.ToHttpStatus(result.Error!.Code));
}

/// <summary>Public handle so in-process tests can host this API (<c>WebApplicationFactory&lt;BackupServerApiMarker&gt;</c>).</summary>
public sealed class BackupServerApiMarker;
