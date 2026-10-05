using Cloud.Contracts;
using Cloud.Contracts.Admin;
using UpdateServer.Application;
using Updates.Contracts;
using Updates.Package;

namespace AdminPortal.Application;

internal static class PackageMapping
{
    public static PackageDto ToDto(this ManagedPackage m)
        => new(m.Package.PackageId, m.Package.PackageType.ToString(), m.Package.TargetId, m.Package.Version, m.Package.TargetFramework,
            m.Package.MinimumHostVersion, m.Package.SizeBytes, m.Package.Sha256, m.KeyId, m.Publisher, m.Status.ToString(),
            m.ReleaseNotes, m.PublishedAt, m.PublishedBy, m.WithdrawnAt);

    public static ModuleDto ToDto(this RegisteredModule m, IEnumerable<ManagedPackage> published)
    {
        var versions = published.Select(p => p.Package.Version).ToList();
        var latest = versions.Count == 0 ? null : versions.OrderByDescending(v => v, Comparer<string>.Create(PackageVersions.Compare)).First();
        return new ModuleDto(m.ModuleId, m.DisplayName, m.Description, m.Category.ToString(), m.IsActive, m.CreatedAt, m.UpdatedAt, latest, versions.Count);
    }
}

/// <summary>
/// The vendor's module registry: which modules exist, which are sold, and (from the package catalog) which versions have
/// been published. Licenses may only entitle registered, active modules.
/// </summary>
public sealed class ModuleRegistryService(
    IModuleRegistryRepository registry,
    IPackageCatalog packages,
    AdminAuditRecorder audit,
    TimeProvider timeProvider)
{
    public async Task<ServiceResult<ModuleDto>> RegisterAsync(AdminActor actor, RegisterModuleRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            return ServiceResult<ModuleDto>.Fail(CloudErrorCodes.Validation, "A request body is required.");

        var id = request.ModuleId?.Trim().ToLowerInvariant() ?? "";
        if (!ManifestRules.IsValidModuleId(id) || id == PackageManifest.CoreTargetId)
            return ServiceResult<ModuleDto>.Fail(CloudErrorCodes.Validation, "ModuleId must be lowercase letters, digits, '.', '-' or '_' and may not be 'core'.");

        var invalid = Check.Text(request.DisplayName, "DisplayName", 100, true) ?? Check.Text(request.Description, "Description", 1000, false);
        if (invalid is not null)
            return ServiceResult<ModuleDto>.Fail(CloudErrorCodes.Validation, invalid);

        if (!TryParseCategory(request.Category, out var category))
            return ServiceResult<ModuleDto>.Fail(CloudErrorCodes.Validation, "Category must be 'Standard' or 'Optional'.");

        if (await registry.FindAsync(id, cancellationToken) is not null)
            return ServiceResult<ModuleDto>.Fail(CloudErrorCodes.Conflict, $"Module '{id}' is already registered.");

        var now = timeProvider.GetUtcNow();
        var module = new RegisteredModule
        {
            ModuleId = id,
            DisplayName = request.DisplayName.Trim(),
            Description = Check.Trimmed(request.Description) ?? "",
            Category = category,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        await registry.AddAsync(module, cancellationToken);
        await audit.RecordAsync(actor, "module.register", "module", id, $"Registered module '{id}' ({category}).", cancellationToken);
        return ServiceResult<ModuleDto>.Ok(module.ToDto([]));
    }

    public async Task<ServiceResult<ModuleDto>> UpdateAsync(AdminActor actor, string moduleId, UpdateModuleRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
            return ServiceResult<ModuleDto>.Fail(CloudErrorCodes.Validation, "A request body is required.");

        var invalid = Check.Text(request.DisplayName, "DisplayName", 100, true) ?? Check.Text(request.Description, "Description", 1000, false);
        if (invalid is not null)
            return ServiceResult<ModuleDto>.Fail(CloudErrorCodes.Validation, invalid);

        var module = await registry.FindAsync(Normalize(moduleId), cancellationToken);
        if (module is null)
            return NotFound();

        module.DisplayName = request.DisplayName.Trim();
        module.Description = Check.Trimmed(request.Description) ?? "";
        module.UpdatedAt = timeProvider.GetUtcNow();

        await registry.SaveAsync(module, cancellationToken);
        await audit.RecordAsync(actor, "module.update", "module", module.ModuleId, $"Updated module '{module.ModuleId}'.", cancellationToken);
        return ServiceResult<ModuleDto>.Ok(await ToDtoAsync(module, cancellationToken));
    }

    /// <summary>Stops NEW licenses and packages for the module. Existing licenses, packages and customer data are untouched.</summary>
    public Task<ServiceResult<ModuleDto>> RetireAsync(AdminActor actor, string moduleId, CancellationToken cancellationToken = default)
        => SetActiveAsync(actor, moduleId, false, cancellationToken);

    public Task<ServiceResult<ModuleDto>> ReactivateAsync(AdminActor actor, string moduleId, CancellationToken cancellationToken = default)
        => SetActiveAsync(actor, moduleId, true, cancellationToken);

    public async Task<ServiceResult<ModuleDetailDto>> GetAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var module = await registry.FindAsync(Normalize(moduleId), cancellationToken);
        if (module is null)
            return ServiceResult<ModuleDetailDto>.Fail(CloudErrorCodes.NotFound, "Unknown module.");

        var all = await packages.ListAsync(module.ModuleId, null, cancellationToken);
        var published = all.Where(p => p.Status == PackageStatus.Published);
        return ServiceResult<ModuleDetailDto>.Ok(new ModuleDetailDto(module.ToDto(published), all.Select(p => p.ToDto()).ToList()));
    }

    public async Task<IReadOnlyList<ModuleDto>> ListAsync(bool includeRetired, CancellationToken cancellationToken = default)
    {
        var modules = await registry.ListAsync(includeRetired, cancellationToken);
        var published = (await packages.ListAsync(null, PackageStatus.Published, cancellationToken))
            .ToLookup(p => p.Package.TargetId, StringComparer.Ordinal);

        return modules.Select(m => m.ToDto(published[m.ModuleId])).ToList();
    }

    private async Task<ServiceResult<ModuleDto>> SetActiveAsync(AdminActor actor, string moduleId, bool active, CancellationToken cancellationToken)
    {
        var module = await registry.FindAsync(Normalize(moduleId), cancellationToken);
        if (module is null)
            return NotFound();

        if (module.IsActive == active)
            return ServiceResult<ModuleDto>.Fail(CloudErrorCodes.InvalidState, active ? "The module is already active." : "The module is already retired.");

        module.IsActive = active;
        module.UpdatedAt = timeProvider.GetUtcNow();

        await registry.SaveAsync(module, cancellationToken);
        await audit.RecordAsync(actor, active ? "module.reactivate" : "module.retire", "module", module.ModuleId,
            $"{(active ? "Reactivated" : "Retired")} module '{module.ModuleId}'.", cancellationToken);
        return ServiceResult<ModuleDto>.Ok(await ToDtoAsync(module, cancellationToken));
    }

    private async Task<ModuleDto> ToDtoAsync(RegisteredModule module, CancellationToken cancellationToken)
        => module.ToDto(await packages.ListAsync(module.ModuleId, PackageStatus.Published, cancellationToken));

    private static bool TryParseCategory(string? text, out ModuleCategory category)
    {
        category = ModuleCategory.Standard;
        return string.IsNullOrWhiteSpace(text) || (Enum.TryParse(text.Trim(), true, out category) && Enum.IsDefined(category));
    }

    private static string Normalize(string moduleId) => moduleId?.Trim().ToLowerInvariant() ?? "";

    private static ServiceResult<ModuleDto> NotFound() => ServiceResult<ModuleDto>.Fail(CloudErrorCodes.NotFound, "Unknown module.");
}

/// <summary>Package publication limits. Values are configuration, not constants.</summary>
public sealed record PackageAdminOptions(long MaxPackageBytes)
{
    public const long DefaultMaxPackageBytes = 512L * 1024 * 1024;

    public static PackageAdminOptions Default { get; } = new(DefaultMaxPackageBytes);
}

/// <summary>
/// Package/update management: publish an already SIGNED package (the server never signs), withdraw it from discovery, restore it.
/// Publication runs the <see cref="PackageInspector"/> gate and the module-registry check; clients still verify everything.
/// </summary>
public sealed class PackageAdminService(
    IPackageCatalog catalog,
    IPackageFileStore files,
    PackageInspector inspector,
    IModuleRegistryRepository registry,
    AdminAuditRecorder audit,
    TimeProvider timeProvider,
    PackageAdminOptions options)
{
    public const int MaxReleaseNotesLength = 4000;

    public async Task<ServiceResult<PackageDto>> PublishAsync(AdminActor actor, Stream content, string? releaseNotes, CancellationToken cancellationToken = default)
    {
        if (Check.Text(releaseNotes, "Release notes", MaxReleaseNotesLength, false) is { } notesError)
            return ServiceResult<PackageDto>.Fail(CloudErrorCodes.Validation, notesError);

        var staged = await files.StageAsync(content, options.MaxPackageBytes, cancellationToken);
        if (staged is null)
            return ServiceResult<PackageDto>.Fail(CloudErrorCodes.TooLarge, $"A package may not exceed {options.MaxPackageBytes} bytes.");

        var committed = false;
        try
        {
            var inspected = inspector.Inspect(staged.Path);
            if (!inspected.IsSuccess)
                return ServiceResult<PackageDto>.From(inspected.Error!);

            var manifest = inspected.Value!.Manifest;

            if (manifest.PackageType == PackageType.Module)
            {
                var module = await registry.FindAsync(manifest.TargetId, cancellationToken);
                if (module is null)
                    return ServiceResult<PackageDto>.Fail(CloudErrorCodes.Validation,
                        $"Module '{manifest.TargetId}' is not in the module registry; register it before publishing packages for it.");

                if (!module.IsActive)
                    return ServiceResult<PackageDto>.Fail(CloudErrorCodes.InvalidState, $"Module '{manifest.TargetId}' is retired.");
            }

            if (await catalog.FindAsync(manifest.PackageId, cancellationToken) is not null)
                return ServiceResult<PackageDto>.Fail(CloudErrorCodes.Conflict, "A package with this ID has already been published.");

            if (await catalog.VersionExistsAsync(manifest.TargetId, manifest.Version, manifest.TargetFramework, cancellationToken))
                return ServiceResult<PackageDto>.Fail(CloudErrorCodes.Conflict,
                    $"Version {manifest.Version} of '{manifest.TargetId}' for {manifest.TargetFramework} is already published.");

            var package = new PublishedPackage(manifest.PackageId, manifest.PackageType, manifest.TargetId, manifest.Version,
                manifest.TargetFramework, manifest.MinimumHostVersion, inspected.Value.Envelope, inspected.Value.SizeBytes, inspected.Value.Sha256);

            var managed = new ManagedPackage(package, manifest.KeyId, manifest.Publisher, PackageStatus.Published,
                Check.Trimmed(releaseNotes), timeProvider.GetUtcNow(), actor.Name, null);

            files.Commit(staged, manifest.PackageId);
            committed = true;
            try
            {
                await catalog.AddAsync(managed, cancellationToken);
            }
            catch
            {
                files.Delete(manifest.PackageId);
                throw;
            }

            await audit.RecordAsync(actor, "package.publish", "package", manifest.PackageId.ToString(),
                $"Published {manifest.TargetId} {manifest.Version} ({manifest.PackageType}).", cancellationToken);
            return ServiceResult<PackageDto>.Ok(managed.ToDto());
        }
        finally
        {
            if (!committed) files.Discard(staged);
        }
    }

    public async Task<IReadOnlyList<PackageDto>> ListAsync(string? targetId, string? status, CancellationToken cancellationToken = default)
    {
        PackageStatus? parsed = Enum.TryParse<PackageStatus>(status, true, out var s) ? s : null;
        var all = await catalog.ListAsync(string.IsNullOrWhiteSpace(targetId) ? null : targetId.Trim().ToLowerInvariant(), parsed, cancellationToken);

        return all
            .OrderBy(p => p.Package.TargetId, StringComparer.Ordinal)
            .ThenByDescending(p => p.Package.Version, Comparer<string>.Create(PackageVersions.Compare))
            .Select(p => p.ToDto())
            .ToList();
    }

    public async Task<ServiceResult<PackageDto>> GetAsync(Guid packageId, CancellationToken cancellationToken = default)
    {
        var package = await catalog.FindAsync(packageId, cancellationToken);
        return package is null ? NotFound() : ServiceResult<PackageDto>.Ok(package.ToDto());
    }

    /// <summary>Removes the package from discovery and download. It stays on record; clients that already installed it are unaffected.</summary>
    public Task<ServiceResult<PackageDto>> WithdrawAsync(AdminActor actor, Guid packageId, string? reason, CancellationToken cancellationToken = default)
        => ChangeStatusAsync(actor, packageId, PackageStatus.Withdrawn, "package.withdraw", reason, cancellationToken);

    public Task<ServiceResult<PackageDto>> RestoreAsync(AdminActor actor, Guid packageId, string? reason, CancellationToken cancellationToken = default)
        => ChangeStatusAsync(actor, packageId, PackageStatus.Published, "package.restore", reason, cancellationToken);

    private async Task<ServiceResult<PackageDto>> ChangeStatusAsync(
        AdminActor actor, Guid packageId, PackageStatus target, string action, string? reason, CancellationToken cancellationToken)
    {
        var package = await catalog.FindAsync(packageId, cancellationToken);
        if (package is null)
            return NotFound();

        if (package.Status == target)
            return ServiceResult<PackageDto>.Fail(CloudErrorCodes.InvalidState,
                target == PackageStatus.Withdrawn ? "The package is already withdrawn." : "The package is already published.");

        await catalog.SetStatusAsync(packageId, target, timeProvider.GetUtcNow(), cancellationToken);

        var because = Check.Trimmed(reason);
        var verb = target == PackageStatus.Withdrawn ? "Withdrew" : "Restored";
        await audit.RecordAsync(actor, action, "package", packageId.ToString(),
            $"{verb} {package.Package.TargetId} {package.Package.Version}{(because is null ? "." : ": " + because)}", cancellationToken);

        var updated = await catalog.FindAsync(packageId, cancellationToken);
        return ServiceResult<PackageDto>.Ok(updated!.ToDto());
    }

    private static ServiceResult<PackageDto> NotFound() => ServiceResult<PackageDto>.Fail(CloudErrorCodes.NotFound, "Unknown package.");
}
