using BackupServer.Application;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using LicenseServer.Application;
using Licensing.Contracts;
using Updates.Package;

namespace AdminPortal.Application;

/// <summary>
/// License administration: creates licenses and changes their commercial state. It works on the existing
/// <see cref="LicenseRecord"/> / <see cref="ILicenseRepository"/> of the license server (no parallel license model) and
/// never signs anything: changes reach clients when they next renew, exactly as for Stage 6.
/// </summary>
public sealed class LicenseAdminService(
    ILicenseRepository licenses,
    ILicenseQuery query,
    ICustomerRepository customers,
    IModuleRegistryRepository registry,
    BackupAccessService backupAccess,
    AdminAuditRecorder audit,
    TimeProvider timeProvider)
{
    public const int MaxTextLength = 100;

    public async Task<ServiceResult<CreateLicenseResponse>> CreateAsync(AdminActor actor, CreateLicenseRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            return ServiceResult<CreateLicenseResponse>.Fail(CloudErrorCodes.Validation, "A request body is required.");

        if (!Guid.TryParse(request.CustomerId, out var customerId))
            return ServiceResult<CreateLicenseResponse>.Fail(CloudErrorCodes.Validation, "CustomerId must be the ID of a customer.");

        var invalid = Check.Text(request.ProductId, "ProductId", MaxTextLength, true);
        if (invalid is not null)
            return ServiceResult<CreateLicenseResponse>.Fail(CloudErrorCodes.Validation, invalid);

        if (request.ValidUntil <= request.ValidFrom)
            return ServiceResult<CreateLicenseResponse>.Fail(CloudErrorCodes.Validation, "ValidUntil must be after ValidFrom.");

        if (request.ValidUntil <= timeProvider.GetUtcNow())
            return ServiceResult<CreateLicenseResponse>.Fail(CloudErrorCodes.Validation, "ValidUntil must be in the future.");

        var modules = NormalizeModules(request.Modules, out var moduleError);
        var features = NormalizeFeatures(request.Features, out var featureError);
        if ((moduleError ?? featureError) is { } entitlementError)
            return ServiceResult<CreateLicenseResponse>.Fail(CloudErrorCodes.Validation, entitlementError);

        var customer = await customers.FindAsync(customerId, cancellationToken);
        if (customer is null)
            return ServiceResult<CreateLicenseResponse>.Fail(CloudErrorCodes.NotFound, "Unknown customer.");

        if (!customer.IsActive)
            return ServiceResult<CreateLicenseResponse>.Fail(CloudErrorCodes.InvalidState, "The customer is inactive; reactivate it before issuing licenses.");

        var unregistered = await UnregisteredModulesAsync(modules, cancellationToken);
        if (unregistered is not null)
            return ServiceResult<CreateLicenseResponse>.Fail(CloudErrorCodes.Validation, unregistered);

        var activationKey = ActivationKeys.Generate();
        var record = new LicenseRecord
        {
            LicenseId = Guid.NewGuid(),
            CustomerId = customer.Id.ToString(),
            ActivationKey = activationKey,
            ProductId = request.ProductId.Trim(),
            ValidFrom = request.ValidFrom,
            ValidUntil = request.ValidUntil,
            Modules = modules,
            Features = features
        };

        await licenses.AddAsync(record, cancellationToken);
        await audit.RecordAsync(actor, "license.create", "license", record.LicenseId.ToString(),
            $"Created license for customer '{customer.Name}' (product '{record.ProductId}', until {record.ValidUntil:O}).", cancellationToken);

        return ServiceResult<CreateLicenseResponse>.Ok(new CreateLicenseResponse(ToDto(record, false), activationKey));
    }

    public async Task<ServiceResult<LicenseDto>> GetAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        var record = await licenses.FindByIdAsync(licenseId, cancellationToken);
        return record is null ? NotFound() : ServiceResult<LicenseDto>.Ok(await ToDtoAsync(record, cancellationToken));
    }

    public async Task<PagedResult<LicenseDto>> ListAsync(string? customerId, string? status, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        LicenseStatusClaim? parsed = Enum.TryParse<LicenseStatusClaim>(status, true, out var s) ? s : null;

        var result = await query.ListAsync(new LicenseFilter(Check.Trimmed(customerId), parsed), p, size, cancellationToken);
        var items = new List<LicenseDto>();
        foreach (var record in result.Items)
            items.Add(await ToDtoAsync(record, cancellationToken));

        return new PagedResult<LicenseDto>(items, p, size, result.Total);
    }

    public async Task<PagedResult<InstallationDto>> ListInstallationsAsync(string? customerId, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var result = await query.ListAsync(new LicenseFilter(Check.Trimmed(customerId), null, true), p, size, cancellationToken);

        var items = result.Items
            .Select(r => new InstallationDto(r.InstallationId!.Value, r.LicenseId, r.CustomerId, r.ProductId, r.Status.ToString(), r.ActivatedAt, r.LastIssuedAt))
            .ToList();
        return new PagedResult<InstallationDto>(items, p, size, result.Total);
    }

    /// <summary>Active to Suspended. The client learns it at its next renewal and evaluates it offline.</summary>
    public Task<ServiceResult<LicenseDto>> SuspendAsync(AdminActor actor, Guid licenseId, string? reason, CancellationToken cancellationToken = default)
        => ChangeStatusAsync(actor, licenseId, "license.suspend", "suspended", reason,
            r => r.Status == LicenseStatusClaim.Active ? null : "Only an active license can be suspended.",
            LicenseStatusClaim.Suspended, cancellationToken);

    /// <summary>Suspended to Active.</summary>
    public Task<ServiceResult<LicenseDto>> ReinstateAsync(AdminActor actor, Guid licenseId, string? reason, CancellationToken cancellationToken = default)
        => ChangeStatusAsync(actor, licenseId, "license.reinstate", "reinstated", reason,
            r => r.Status == LicenseStatusClaim.Suspended ? null : "Only a suspended license can be reinstated.",
            LicenseStatusClaim.Active, cancellationToken);

    /// <summary>Active or Suspended to Revoked. Revocation is terminal.</summary>
    public Task<ServiceResult<LicenseDto>> RevokeAsync(AdminActor actor, Guid licenseId, string? reason, CancellationToken cancellationToken = default)
        => ChangeStatusAsync(actor, licenseId, "license.revoke", "revoked", reason,
            r => r.Status == LicenseStatusClaim.Revoked ? "The license is already revoked." : null,
            LicenseStatusClaim.Revoked, cancellationToken);

    public Task<ServiceResult<LicenseDto>> ExtendAsync(AdminActor actor, Guid licenseId, ExtendLicenseRequest request, CancellationToken cancellationToken = default)
        => MutateAsync(actor, licenseId, "license.extend", cancellationToken, record =>
        {
            if (request is null) return ("A request body is required.", CloudErrorCodes.Validation, null);
            if (record.Status == LicenseStatusClaim.Revoked) return ("A revoked license cannot be extended.", CloudErrorCodes.InvalidState, null);
            if (request.ValidUntil <= record.ValidUntil) return ("The new expiry must be later than the current one.", CloudErrorCodes.Validation, null);

            var summary = $"Extended license from {record.ValidUntil:O} to {request.ValidUntil:O}.";
            record.ValidUntil = request.ValidUntil;
            return (null, null, summary);
        });

    public async Task<ServiceResult<LicenseDto>> SetEntitlementsAsync(AdminActor actor, Guid licenseId, SetEntitlementsRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            return ServiceResult<LicenseDto>.Fail(CloudErrorCodes.Validation, "A request body is required.");

        var modules = NormalizeModules(request.Modules, out var moduleError);
        var features = NormalizeFeatures(request.Features, out var featureError);
        if ((moduleError ?? featureError) is { } entitlementError)
            return ServiceResult<LicenseDto>.Fail(CloudErrorCodes.Validation, entitlementError);

        var existing = await licenses.FindByIdAsync(licenseId, cancellationToken);
        if (existing is null)
            return NotFound();

        // Removing a module is always allowed; only modules being ADDED must be registered and active.
        var added = modules.Where(m => !existing.Modules.Contains(m, StringComparer.Ordinal)).ToList();
        var unregistered = await UnregisteredModulesAsync(added, cancellationToken);
        if (unregistered is not null)
            return ServiceResult<LicenseDto>.Fail(CloudErrorCodes.Validation, unregistered);

        return await MutateAsync(actor, licenseId, "license.entitlements", cancellationToken, record =>
        {
            if (record.Status == LicenseStatusClaim.Revoked) return ("A revoked license cannot be changed.", CloudErrorCodes.InvalidState, null);

            record.Modules = modules;
            record.Features = features;
            return (null, null, $"Entitlements set to modules [{string.Join(", ", modules)}], features [{string.Join(", ", features)}].");
        });
    }

    /// <summary>Unbinds the license from its installation so another installation can activate it (transfer / reinstall).</summary>
    public Task<ServiceResult<LicenseDto>> ReleaseInstallationAsync(AdminActor actor, Guid licenseId, CancellationToken cancellationToken = default)
        => MutateAsync(actor, licenseId, "license.release-installation", cancellationToken, record =>
        {
            if (record.InstallationId is null) return ("The license is not bound to an installation.", CloudErrorCodes.InvalidState, null);

            var summary = $"Released installation {record.InstallationId}.";
            record.InstallationId = null;
            record.ActivatedAt = null;
            return (null, null, summary);
        });

    /// <summary>Creates or rotates the license's backup access token. The plaintext is returned ONCE.</summary>
    public async Task<ServiceResult<BackupTokenResponse>> IssueBackupTokenAsync(AdminActor actor, Guid licenseId, CancellationToken cancellationToken = default)
    {
        var issued = await backupAccess.IssueTokenAsync(licenseId, cancellationToken);
        if (!issued.IsSuccess)
            return ServiceResult<BackupTokenResponse>.From(issued.Error!);

        await audit.RecordAsync(actor, "license.backup-token.issue", "license", licenseId.ToString(), "Issued a backup access token.", cancellationToken);
        return ServiceResult<BackupTokenResponse>.Ok(new BackupTokenResponse(licenseId, issued.Value!));
    }

    public async Task<ServiceResult> RevokeBackupTokenAsync(AdminActor actor, Guid licenseId, CancellationToken cancellationToken = default)
    {
        if (!await backupAccess.RevokeTokenAsync(licenseId, cancellationToken))
            return ServiceResult.Fail(CloudErrorCodes.NotFound, "The license has no backup access token.");

        await audit.RecordAsync(actor, "license.backup-token.revoke", "license", licenseId.ToString(), "Revoked the backup access token.", cancellationToken);
        return ServiceResult.Ok();
    }

    // ---------------------------------------------------------------------------------------------------------------

    private Task<ServiceResult<LicenseDto>> ChangeStatusAsync(
        AdminActor actor, Guid licenseId, string action, string verb, string? reason,
        Func<LicenseRecord, string?> precondition, LicenseStatusClaim target, CancellationToken cancellationToken)
        => MutateAsync(actor, licenseId, action, cancellationToken, record =>
        {
            var blocked = precondition(record);
            if (blocked is not null) return (blocked, CloudErrorCodes.InvalidState, null);

            record.Status = target;
            var because = Check.Trimmed(reason);
            return (null, null, because is null ? $"License {verb}." : $"License {verb}: {because}");
        });

    /// <summary>Loads the license, applies <paramref name="change"/> (which returns an error or an audit summary), saves, audits.</summary>
    private async Task<ServiceResult<LicenseDto>> MutateAsync(
        AdminActor actor, Guid licenseId, string action, CancellationToken cancellationToken,
        Func<LicenseRecord, (string? Error, string? Code, string? Summary)> change)
    {
        var record = await licenses.FindByIdAsync(licenseId, cancellationToken);
        if (record is null)
            return NotFound();

        var (error, code, summary) = change(record);
        if (error is not null)
            return ServiceResult<LicenseDto>.Fail(code!, error);

        try
        {
            await licenses.SaveAsync(record, cancellationToken);
        }
        catch (LicenseConcurrencyException)
        {
            return ServiceResult<LicenseDto>.Fail(CloudErrorCodes.Conflict, "The license was changed by someone else; reload it and try again.");
        }

        await audit.RecordAsync(actor, action, "license", licenseId.ToString(), summary!, cancellationToken);
        return ServiceResult<LicenseDto>.Ok(await ToDtoAsync(record, cancellationToken));
    }

    private async Task<string?> UnregisteredModulesAsync(IReadOnlyCollection<string> moduleIds, CancellationToken cancellationToken)
    {
        if (moduleIds.Count == 0) return null;

        var found = (await registry.FindManyAsync(moduleIds, cancellationToken)).ToDictionary(m => m.ModuleId, StringComparer.Ordinal);
        var bad = moduleIds.Where(id => !found.TryGetValue(id, out var m) || !m.IsActive).ToList();
        return bad.Count == 0
            ? null
            : $"Module(s) not registered or retired: {string.Join(", ", bad)}. Register them in the module registry first.";
    }

    private static List<string> NormalizeModules(IReadOnlyList<string>? input, out string? error)
    {
        error = null;
        var result = new List<string>();
        foreach (var raw in input ?? [])
        {
            var id = raw?.Trim().ToLowerInvariant() ?? "";
            if (!ManifestRules.IsValidModuleId(id))
            {
                error = $"'{raw}' is not a valid module ID (lowercase letters, digits, '.', '-', '_').";
                return result;
            }

            if (!result.Contains(id, StringComparer.Ordinal)) result.Add(id);
        }

        return result;
    }

    private static List<string> NormalizeFeatures(IReadOnlyList<string>? input, out string? error)
    {
        error = null;
        var result = new List<string>();
        foreach (var raw in input ?? [])
        {
            var id = raw?.Trim() ?? "";
            if (id.Length is 0 or > MaxTextLength || id.Any(char.IsWhiteSpace))
            {
                error = $"'{raw}' is not a valid feature ID (non-empty, at most {MaxTextLength} characters, no whitespace).";
                return result;
            }

            if (!result.Contains(id, StringComparer.OrdinalIgnoreCase)) result.Add(id);
        }

        return result;
    }

    private async Task<LicenseDto> ToDtoAsync(LicenseRecord record, CancellationToken cancellationToken)
        => ToDto(record, await backupAccess.HasTokenAsync(record.LicenseId, cancellationToken));

    private static LicenseDto ToDto(LicenseRecord r, bool hasBackupAccess)
        => new(r.LicenseId, r.CustomerId, r.ProductId, r.Status.ToString(), r.ValidFrom, r.ValidUntil,
            r.Modules, r.Features, r.InstallationId, r.ActivatedAt, r.LastIssuedAt, r.Version, hasBackupAccess);

    private static ServiceResult<LicenseDto> NotFound()
        => ServiceResult<LicenseDto>.Fail(CloudErrorCodes.NotFound, "Unknown license.");
}
