using Client.Updater.Domain;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Modules;
using Platform.Core.Modules;
using Platform.Core.Results;
using Updates.Contracts;

namespace Client.Updater.Application;

/// <summary>
/// PKG-01b: what GenericPOS.exe (the launcher) told this process: where it is, so a restart goes through it and uses the active versions,
/// and which active versions it did not start and why ("target@version: reason" lines). Empty when the application was started directly
/// (development, or an installation without the launcher): activated updates are then not used.
/// </summary>
/// <param name="LaidOver">The modules the launcher laid over the installed application (module id -> version): a module NOT listed runs as installed.</param>
public sealed record LauncherHandOff(string? LauncherPath, IReadOnlyDictionary<string, string> HeldBack, IReadOnlyDictionary<string, string>? LaidOver = null)
{
    public const string LauncherPathVariable = "GENERICPOS_LAUNCHER_PATH";
    public const string HeldBackVariable = "GENERICPOS_LAUNCHER_HELD_BACK";
    public const string ModulesVariable = "GENERICPOS_LAUNCHER_MODULES";

    public static LauncherHandOff None { get; } = new(null, new Dictionary<string, string>());

    /// <summary>True when this module runs from an update the launcher laid over the installed application.</summary>
    public bool RunsUpdated(string moduleId) => LaidOver?.ContainsKey(moduleId) == true;

    public bool StartedByLauncher => !string.IsNullOrWhiteSpace(LauncherPath);

    public static LauncherHandOff FromEnvironment(Func<string, string?> environment)
    {
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (environment(HeldBackVariable) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0) held[line[..colon]] = line[(colon + 2)..];
        }

        var laidOver = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (environment(ModulesVariable) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.Split('@') is [{ Length: > 0 } module, { Length: > 0 } version]) laidOver[module] = version;

        return new LauncherHandOff(environment(LauncherPathVariable) is { Length: > 0 } path ? path : null, held, laidOver);
    }

    /// <summary>Why the launcher did not start this version, or null.</summary>
    public string? WhyNotStarted(string targetId, string version) => HeldBack.GetValueOrDefault(targetId + "@" + version);
}

/// <summary>Where a part of the application stands with its updates.</summary>
public enum UpdatePartState
{
    /// <summary>The active version runs.</summary>
    InUse = 1,

    /// <summary>A newer (or older, after going back) version is active and is used after a restart.</summary>
    WaitsForRestart = 2,

    /// <summary>The launcher did not start the active version (the reason is given); the application's own recovery rolls it back.</summary>
    NotStarted = 3,

    /// <summary>The active version is used only when the application is started by GenericPOS.exe.</summary>
    WaitsForLauncher = 4,

    /// <summary>An update changed the data and could not be finished or undone safely: the vendor must decide (RecoveryRequired).</summary>
    NeedsAttention = 5
}

/// <param name="TargetId">"core" or a module id.</param>
/// <param name="Name">The module's name; empty for the core (the screen says "Application").</param>
/// <param name="Running">The version this process runs, or null when nothing of it runs.</param>
/// <param name="Active">The version the updater made active; with no update active, the version that came with the application, or null when
/// that is not the one running (an update was rolled back while it ran: its number is not known here).</param>
/// <param name="CanGoBack">An installed update of this part can be rolled back (to the version before it, or the built-in one).</param>
public sealed record UpdatePart(string TargetId, string Name, string? Running, string? Active, UpdatePartState State, string? Reason, bool CanGoBack);

/// <summary>PKG-01b: the versions in use against the versions made active - for the Updates screen and the shell's "Restart now" notice.</summary>
public interface IUpdateAdoption
{
    LauncherHandOff Launcher { get; }

    IReadOnlyList<UpdatePart> Parts();

    /// <summary>Raised after an update was installed or rolled back in this process (the shell refreshes its notice).</summary>
    event EventHandler? Changed;

    void NotifyChanged();
}

public sealed class UpdateAdoption(IUpdateStore store, IRunningVersions running, IModuleRegistry registry, LauncherHandOff launcher, ModuleVersion baselineHostVersion) : IUpdateAdoption
{
    public LauncherHandOff Launcher { get; } = launcher;

    public event EventHandler? Changed;

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<UpdatePart> Parts()
    {
        var journals = Directory.Exists(store.Root) ? store.ListJournals() : [];
        var parts = new List<UpdatePart> { Part(PackageManifest.CoreTargetId, string.Empty, running.Of(PackageManifest.CoreTargetId)?.ToString(), baselineHostVersion.ToString(), journals) };

        var modules = registry.GetAll().Select(m => m.Manifest).OrderBy(m => m.Name, StringComparer.CurrentCulture).ToList();
        // a module running from an update has, as its built-in version, the one installed with the application (not the one running)
        parts.AddRange(modules.Select(m => Part(m.ModuleId.Value, m.Name, m.Version.ToString(), Launcher.RunsUpdated(m.ModuleId.Value) ? null : m.Version.ToString(), journals)));

        // a module installed by an update that this application does not contain (it needs an application update to run)
        var known = new HashSet<string>(modules.Select(m => m.ModuleId.Value)) { PackageManifest.CoreTargetId };
        foreach (var target in journals.Select(j => j.TargetId).Distinct().Where(t => !known.Contains(t)).Order(StringComparer.Ordinal))
            if (store.ReadActive(target) is not null)
                parts.Add(Part(target, target, null, "-", journals));

        return parts;
    }

    private UpdatePart Part(string target, string name, string? runningVersion, string? builtIn, IReadOnlyList<UpdateJournal> journals)
    {
        var active = store.ReadActive(target)?.Version ?? builtIn;
        var mine = journals.Where(j => j.TargetId == target).ToList();
        var canGoBack = mine.Any(j => j.Version == active && j.State is UpdateState.Activated or UpdateState.Confirmed or UpdateState.RecoveryRequired);

        if (mine.Where(j => j.State == UpdateState.RecoveryRequired).OrderByDescending(j => j.History.LastOrDefault()?.At).FirstOrDefault() is { } stuck)
            return new UpdatePart(target, name, runningVersion, active, UpdatePartState.NeedsAttention, stuck.History.LastOrDefault()?.Message, canGoBack);

        if (string.Equals(runningVersion, active, StringComparison.Ordinal))
            return new UpdatePart(target, name, runningVersion, active, UpdatePartState.InUse, null, canGoBack);

        if (active is not null && Launcher.WhyNotStarted(target, active) is { } reason)
            return new UpdatePart(target, name, runningVersion, active, UpdatePartState.NotStarted, reason, canGoBack);

        return new UpdatePart(target, name, runningVersion, active,
            Launcher.StartedByLauncher ? UpdatePartState.WaitsForRestart : UpdatePartState.WaitsForLauncher, null, canGoBack);
    }
}

// ------------------------------------------------------------------ the Updates screen's entry points (updates.manage)

public sealed record GetUpdateOverviewQuery;

/// <param name="StartedByLauncher">Activated updates are used after a restart only when the application was started by GenericPOS.exe.</param>
public sealed record UpdateOverview(IReadOnlyList<UpdatePart> Parts, bool StartedByLauncher)
{
    public bool RestartWouldHelp => StartedByLauncher && Parts.Any(p => p.State == UpdatePartState.WaitsForRestart);
}

public sealed class GetUpdateOverviewQueryHandler(IUpdateAdoption adoption, IAuthorizationService authorization)
{
    public async Task<Result<UpdateOverview>> HandleAsync(GetUpdateOverviewQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(UpdatesCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<UpdateOverview>(allowed.Error);
        return new UpdateOverview(adoption.Parts(), adoption.Launcher.StartedByLauncher);
    }
}

public sealed record CheckForUpdatesQuery;

/// <summary>Asks the update server what there is (verified, installable and entitled updates only - Stage 7). Needs updates.manage.</summary>
public sealed class CheckForUpdatesQueryHandler(IUpdateService updates, IAuthorizationService authorization)
{
    public async Task<Result<IReadOnlyList<UpdateInfo>>> HandleAsync(CheckForUpdatesQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(UpdatesCapabilities.Manage, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<IReadOnlyList<UpdateInfo>>(allowed.Error);
        return await updates.CheckForUpdatesAsync(cancellationToken);
    }
}
