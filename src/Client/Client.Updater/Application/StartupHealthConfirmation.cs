using Client.Updater.Domain;
using Microsoft.Extensions.Logging;
using Platform.Core.Modules;

namespace Client.Updater.Application;

/// <summary>The versions THIS process actually runs (not what is merely activated on disk).</summary>
public interface IRunningVersions
{
    /// <summary>The running version of a target ("core" or a module id), or null when nothing of it runs here.</summary>
    ModuleVersion? Of(string targetId);
}

/// <summary>
/// FIX-03: tells the updater that the application started healthy, so an activated update becomes final instead of being rolled
/// back after <see cref="UpdaterOptions.MaxStartupAttempts"/> starts.
///
/// Decisions (user, 2026-10-08):
///   1. Only what really runs is confirmed: an activated update is confirmed when the version running in this process equals the
///      activated version. An update that is activated on disk but not loaded (until the PKG-01 launcher exists, the application runs
///      its built-in binaries) is NOT confirmed - it never ran, so it is not proven - and startup recovery rolls it back as designed.
///   2. "Healthy" means the host started (every hosted service, including database migrations and the fail-fast module lifecycle)
///      and the start screen was shown; nobody has to sign in.
///
/// Runs once per process and never throws: a confirmation problem is logged and the application carries on.
/// </summary>
public sealed class StartupHealthConfirmation(
    IUpdateStore store,
    IUpdateService updates,
    IRunningVersions running,
    UpdaterOptions options,
    ILogger<StartupHealthConfirmation> logger)
{
    private int _started;

    /// <summary>Confirms every activated update whose version runs now. Returns the confirmed target ids (empty after the first call).</summary>
    public async Task<IReadOnlyList<string>> ConfirmAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return [];

        var confirmed = new List<string>();
        try
        {
            if (!Directory.Exists(store.Root)) return confirmed;

            foreach (var journal in store.ListJournals().Where(j => j.State == UpdateState.Activated).GroupBy(j => j.TargetId).Select(Latest))
            {
                var runningVersion = running.Of(journal.TargetId);
                var activeVersion = store.ReadActive(journal.TargetId)?.Version;
                var runs = runningVersion is not null
                    && ModuleVersion.TryParse(journal.Version, out var activated) && runningVersion.Equals(activated)
                    && string.Equals(activeVersion, journal.Version, StringComparison.Ordinal);

                if (!runs)
                {
                    logger.LogWarning(
                        "Update {Target} {Version} is activated but this process runs {Running}; it is not confirmed (start {Attempt} of {Max} before it is rolled back).",
                        journal.TargetId, journal.Version, runningVersion?.ToString() ?? "nothing of it", journal.StartupAttempts, options.MaxStartupAttempts);
                    continue;
                }

                var result = await updates.ConfirmHealthyAsync(journal.TargetId, cancellationToken);
                if (result.IsSuccess) confirmed.Add(journal.TargetId);
                else logger.LogWarning("Update {Target} {Version} could not be confirmed: {Error}", journal.TargetId, journal.Version, result.Error.Description);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Confirming the healthy start failed; the application continues. Unconfirmed updates are rolled back by startup recovery.");
        }

        return confirmed;
    }

    /// <summary>The same journal <see cref="IUpdateService.ConfirmHealthyAsync"/> confirms: the most recently changed one.</summary>
    private static UpdateJournal Latest(IEnumerable<UpdateJournal> journals)
        => journals.OrderByDescending(j => j.History.LastOrDefault()?.At).First();
}
