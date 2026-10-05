using AdminPortal.Application;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using Cloud.Hosting;
using Cloud.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

// Vendor administration API (Stage 9). Thin endpoints over AdminPortal.Application.
//
//   Authentication: every /api/admin request needs  Authorization: Bearer <admin API key>.
//   Keys are configured as NAME + SHA-256 of the key (never the key):  AdminPortal:Keys:0:Name / AdminPortal:Keys:0:Sha256
//   No configured key = nobody can call the API. In the Development environment ONLY, a missing key list generates an
//   EPHEMERAL key whose plaintext is logged once at startup.
//
//   Configuration: CloudDatabase:ConnectionString, UpdateServer:PackageDirectory, BackupServer:StorageDirectory,
//   UpdateServer:TrustedKeys (optional), UpdateServer:MaxPackageBytes (optional). See PROJECT_STATE.md.
//
// Production transport is HTTPS (HSTS + redirection are enabled outside Development).
// NOT included (later stages): a browser UI, password/SSO/2FA, per-administrator roles, rate limiting (Stage 11).
var builder = WebApplication.CreateBuilder(args);

string? developmentKey = null;
if (builder.Environment.IsDevelopment() && !builder.Configuration.GetSection("AdminPortal:Keys").GetChildren().Any())
{
    developmentKey = AdminKeys.Generate();
    builder.Configuration["AdminPortal:Keys:0:Name"] = "development";
    builder.Configuration["AdminPortal:Keys:0:Sha256"] = AdminKeys.Hash(developmentKey);
}

builder.Services.AddCloudDatabase(builder.Configuration);
builder.Services.AddCloudSecurity(builder.Configuration);
builder.Services.AddAdminPortalServices(builder.Configuration);

// Outside Development the portal refuses to start unless it knows which publisher keys to trust: without them it could not tell a forged
// package from a real one at publication (clients would still reject it, but a vendor must not distribute what it cannot verify).
if (!builder.Environment.IsDevelopment() && !builder.Configuration.GetSection("UpdateServer:TrustedKeys").GetChildren().Any())
    throw new InvalidOperationException("UpdateServer:TrustedKeys is required outside the Development environment (packages are verified when they are published).");

var app = builder.Build();

app.UseCloudSecurityHeaders();

if (developmentKey is not null)
    app.Logger.LogWarning("DEVELOPMENT ONLY: no administrator keys are configured; using the ephemeral key '{Key}' (name 'development').", developmentKey);

app.UseCloudTransportSecurity();   // HSTS, HTTPS redirection and a hard refusal of plain HTTP outside Development

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var admin = app.MapGroup("/api/admin").AddEndpointFilter<AdminAuthenticationFilter>();

// ---- dashboard & audit -----------------------------------------------------------------------------------------------
admin.MapGet("/dashboard", async (AdminOperationsService ops, CancellationToken ct) => Results.Ok(await ops.GetDashboardAsync(ct)));

admin.MapGet("/audit", async (AdminOperationsService ops, string? actor, string? action, string? entityType, string? entityId,
    DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize, CancellationToken ct)
    => Results.Ok(await ops.QueryAuditAsync(new AuditFilter(actor, action, entityType, entityId, from, to), page, pageSize, ct)));

// ---- customers -------------------------------------------------------------------------------------------------------
admin.MapPost("/customers", async (HttpContext http, CustomerRequest request, CustomerAdminService svc, CancellationToken ct) =>
{
    var r = await svc.CreateAsync(http.Admin(), request, ct);
    return r.IsSuccess ? Results.Created($"/api/admin/customers/{r.Value!.Id}", r.Value) : r.Fail();
});

admin.MapGet("/customers", async (CustomerAdminService svc, string? search, bool? includeInactive, int? page, int? pageSize, CancellationToken ct)
    => Results.Ok(await svc.ListAsync(search, includeInactive ?? false, page, pageSize, ct)));

admin.MapGet("/customers/{id:guid}", async (Guid id, CustomerAdminService svc, CancellationToken ct) => (await svc.GetAsync(id, ct)).ToResult());

admin.MapPut("/customers/{id:guid}", async (HttpContext http, Guid id, CustomerRequest request, CustomerAdminService svc, CancellationToken ct)
    => (await svc.UpdateAsync(http.Admin(), id, request, ct)).ToResult());

admin.MapPost("/customers/{id:guid}/deactivate", async (HttpContext http, Guid id, CustomerAdminService svc, CancellationToken ct)
    => (await svc.DeactivateAsync(http.Admin(), id, ct)).ToResult());

admin.MapPost("/customers/{id:guid}/reactivate", async (HttpContext http, Guid id, CustomerAdminService svc, CancellationToken ct)
    => (await svc.ReactivateAsync(http.Admin(), id, ct)).ToResult());

// ---- licenses & installations ----------------------------------------------------------------------------------------
admin.MapPost("/licenses", async (HttpContext http, CreateLicenseRequest request, LicenseAdminService svc, CancellationToken ct) =>
{
    var r = await svc.CreateAsync(http.Admin(), request, ct);
    return r.IsSuccess ? Results.Created($"/api/admin/licenses/{r.Value!.License.LicenseId}", r.Value) : r.Fail();
});

admin.MapGet("/licenses", async (LicenseAdminService svc, string? customerId, string? status, int? page, int? pageSize, CancellationToken ct)
    => Results.Ok(await svc.ListAsync(customerId, status, page, pageSize, ct)));

admin.MapGet("/licenses/{id:guid}", async (Guid id, LicenseAdminService svc, CancellationToken ct) => (await svc.GetAsync(id, ct)).ToResult());

admin.MapPost("/licenses/{id:guid}/suspend", async (HttpContext http, Guid id, StatusChangeRequest? body, LicenseAdminService svc, CancellationToken ct)
    => (await svc.SuspendAsync(http.Admin(), id, body?.Reason, ct)).ToResult());

admin.MapPost("/licenses/{id:guid}/reinstate", async (HttpContext http, Guid id, StatusChangeRequest? body, LicenseAdminService svc, CancellationToken ct)
    => (await svc.ReinstateAsync(http.Admin(), id, body?.Reason, ct)).ToResult());

admin.MapPost("/licenses/{id:guid}/revoke", async (HttpContext http, Guid id, StatusChangeRequest? body, LicenseAdminService svc, CancellationToken ct)
    => (await svc.RevokeAsync(http.Admin(), id, body?.Reason, ct)).ToResult());

admin.MapPost("/licenses/{id:guid}/extend", async (HttpContext http, Guid id, ExtendLicenseRequest request, LicenseAdminService svc, CancellationToken ct)
    => (await svc.ExtendAsync(http.Admin(), id, request, ct)).ToResult());

admin.MapPut("/licenses/{id:guid}/entitlements", async (HttpContext http, Guid id, SetEntitlementsRequest request, LicenseAdminService svc, CancellationToken ct)
    => (await svc.SetEntitlementsAsync(http.Admin(), id, request, ct)).ToResult());

admin.MapPost("/licenses/{id:guid}/release-installation", async (HttpContext http, Guid id, LicenseAdminService svc, CancellationToken ct)
    => (await svc.ReleaseInstallationAsync(http.Admin(), id, ct)).ToResult());

admin.MapPost("/licenses/{id:guid}/backup-token", async (HttpContext http, Guid id, LicenseAdminService svc, CancellationToken ct)
    => (await svc.IssueBackupTokenAsync(http.Admin(), id, ct)).ToResult());

admin.MapDelete("/licenses/{id:guid}/backup-token", async (HttpContext http, Guid id, LicenseAdminService svc, CancellationToken ct)
    => (await svc.RevokeBackupTokenAsync(http.Admin(), id, ct)).ToNoContent());

admin.MapGet("/installations", async (LicenseAdminService svc, string? customerId, int? page, int? pageSize, CancellationToken ct)
    => Results.Ok(await svc.ListInstallationsAsync(customerId, page, pageSize, ct)));

// ---- module registry -------------------------------------------------------------------------------------------------
admin.MapPost("/modules", async (HttpContext http, RegisterModuleRequest request, ModuleRegistryService svc, CancellationToken ct) =>
{
    var r = await svc.RegisterAsync(http.Admin(), request, ct);
    return r.IsSuccess ? Results.Created($"/api/admin/modules/{r.Value!.ModuleId}", r.Value) : r.Fail();
});

admin.MapGet("/modules", async (ModuleRegistryService svc, bool? includeRetired, CancellationToken ct)
    => Results.Ok(await svc.ListAsync(includeRetired ?? false, ct)));

admin.MapGet("/modules/{moduleId}", async (string moduleId, ModuleRegistryService svc, CancellationToken ct) => (await svc.GetAsync(moduleId, ct)).ToResult());

admin.MapPut("/modules/{moduleId}", async (HttpContext http, string moduleId, UpdateModuleRequest request, ModuleRegistryService svc, CancellationToken ct)
    => (await svc.UpdateAsync(http.Admin(), moduleId, request, ct)).ToResult());

admin.MapPost("/modules/{moduleId}/retire", async (HttpContext http, string moduleId, ModuleRegistryService svc, CancellationToken ct)
    => (await svc.RetireAsync(http.Admin(), moduleId, ct)).ToResult());

admin.MapPost("/modules/{moduleId}/reactivate", async (HttpContext http, string moduleId, ModuleRegistryService svc, CancellationToken ct)
    => (await svc.ReactivateAsync(http.Admin(), moduleId, ct)).ToResult());

// ---- packages / updates ----------------------------------------------------------------------------------------------
// The request body IS the signed .gpkg file (application/octet-stream). The server never signs and never trusts it blindly.
admin.MapPost("/packages", async (HttpContext http, PackageAdminService svc, PackageAdminOptions options, string? releaseNotes, CancellationToken ct) =>
{
    var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = options.MaxPackageBytes;

    var r = await svc.PublishAsync(http.Admin(), http.Request.Body, releaseNotes, ct);
    return r.IsSuccess ? Results.Created($"/api/admin/packages/{r.Value!.PackageId}", r.Value) : r.Fail();
});

admin.MapGet("/packages", async (PackageAdminService svc, string? targetId, string? status, CancellationToken ct)
    => Results.Ok(await svc.ListAsync(targetId, status, ct)));

admin.MapGet("/packages/{id:guid}", async (Guid id, PackageAdminService svc, CancellationToken ct) => (await svc.GetAsync(id, ct)).ToResult());

admin.MapPost("/packages/{id:guid}/withdraw", async (HttpContext http, Guid id, StatusChangeRequest? body, PackageAdminService svc, CancellationToken ct)
    => (await svc.WithdrawAsync(http.Admin(), id, body?.Reason, ct)).ToResult());

admin.MapPost("/packages/{id:guid}/restore", async (HttpContext http, Guid id, StatusChangeRequest? body, PackageAdminService svc, CancellationToken ct)
    => (await svc.RestoreAsync(http.Admin(), id, body?.Reason, ct)).ToResult());

// ---- stored backups (the vendor's view; content is never exposed here) -----------------------------------------------
admin.MapGet("/backups", async (AdminOperationsService ops, Guid? licenseId, int? page, int? pageSize, CancellationToken ct)
    => Results.Ok(await ops.ListBackupsAsync(licenseId, page, pageSize, ct)));

admin.MapDelete("/backups/{id:guid}", async (HttpContext http, Guid id, AdminOperationsService ops, CancellationToken ct)
    => (await ops.DeleteBackupAsync(http.Admin(), id, ct)).ToNoContent());

app.Run();

/// <summary>Authenticates /api/admin requests by API key and keeps administrative responses out of caches.</summary>
internal sealed class AdminAuthenticationFilter(AdminKeyAuthenticator authenticator, AuthenticationThrottle throttle, ICloudSecurityLog securityLog) : IEndpointFilter
{
    public const string ActorKey = "admin.actor";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";

        var header = http.Request.Headers.Authorization.ToString();
        var key = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..] : null;

        var caller = http.CallerKey();
        var decision = throttle.Check(caller);
        if (!decision.Allowed)
            return http.TooManyRequests(decision);

        var actor = authenticator.Authenticate(key);
        if (actor is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            var blocked = throttle.RecordFailure(caller);
            await securityLog.RecordAsync("anonymous", blocked ? "admin.auth-blocked" : "admin.auth-failed", "admin-access", caller,
                blocked ? "Too many failed administrator-key attempts; the caller was blocked for a while." : "A request presented a missing or invalid administrator key.");
            return Results.Json(new ApiError(CloudErrorCodes.Unauthorized, "A valid administrator API key is required."), statusCode: 401);
        }

        throttle.RecordSuccess(caller);
        http.Items[ActorKey] = actor;
        return await next(context);
    }
}

internal static class AdminHttpExtensions
{
    public static AdminActor Admin(this HttpContext http) => (AdminActor)http.Items[AdminAuthenticationFilter.ActorKey]!;

    public static IResult Fail(this ServiceResult result)
        => Results.Json(result.Error!, statusCode: CloudErrorCodes.ToHttpStatus(result.Error!.Code));

    public static IResult ToResult<T>(this ServiceResult<T> result) => result.IsSuccess ? Results.Ok(result.Value) : result.Fail();

    public static IResult ToNoContent(this ServiceResult result) => result.IsSuccess ? Results.NoContent() : result.Fail();
}

/// <summary>Public handle so in-process tests can host this API (<c>WebApplicationFactory&lt;AdminPortalApiMarker&gt;</c>).</summary>
public sealed class AdminPortalApiMarker;
