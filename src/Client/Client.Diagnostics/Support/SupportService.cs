using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Client.Diagnostics.Crash;
using Client.Diagnostics.Logging;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Licensing;
using Platform.Application.Modules;
using Platform.Core.Results;

namespace Client.Diagnostics.Support;

/// <summary>The support capability (MISS-05). Sensitive (the logs name users, products and errors); no license needed, like restoring.</summary>
public static class SupportCapabilities
{
    public const string Module = "support";

    public const string Export = "support.export";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(Export, Module, "Save a support file", "See the crash reports and save the logs and crash reports as a file to send to the vendor.",
            LicenseRequirement.None, IsSensitive: true),
    ];
}

public sealed class SupportCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => SupportCapabilities.All;
}

public static class SupportErrorCodes
{
    public const string Failed = "Support.Failed";
    public const string InvalidTarget = "Support.InvalidTarget";
}

/// <summary>What the Support screen shows.</summary>
public sealed record SupportOverview(string LogFolder, int LogFileCount, long LogBytes, IReadOnlyList<CrashReportInfo> CrashReports, int UnseenCrashes);

/// <summary>A saved support file.</summary>
public sealed record SupportFileSaved(string Path, int LogFiles, int CrashReports, long Bytes);

/// <summary>
/// The support side of diagnostics (MISS-05): what is kept, and the support file - a .zip of the log files, the crash reports and
/// <c>about.txt</c> (versions, modules, license state), saved where the person chooses, to send to the vendor. It only ever takes files
/// from the Logs folder by their known names: never the database, a backup or a key file. Nothing is uploaded anywhere.
/// </summary>
public sealed class SupportService(
    RollingLogWriter writer,
    CrashReporter crashes,
    TimeProvider clock,
    ILogger<SupportService> logger,
    IModuleRegistry? modules = null,
    ILicenseEntitlementService? licensing = null,
    IBusinessEventSink? events = null) : ICrashNoticeSource
{
    public SupportOverview GetOverview()
    {
        var logs = RollingLogWriter.LogFiles(writer.Folders.Logs).ToList();
        return new SupportOverview(writer.Folders.Logs, logs.Count, logs.Sum(l => l.Info.Length), crashes.List(), crashes.Unseen().Count);
    }

    public IReadOnlyList<CrashReportInfo> UnseenCrashes() => crashes.Unseen();

    public void MarkCrashesSeen()
    {
        crashes.MarkSeen();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public async Task<Result<SupportFileSaved>> SaveSupportFileAsync(string? target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
            return Error.Validation(SupportErrorCodes.InvalidTarget, "Choose where to save the support file.");
        var path = target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? target : target + ".zip";
        var logFolder = Path.GetFullPath(writer.Folders.Logs) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(path).StartsWith(logFolder, StringComparison.OrdinalIgnoreCase))
            return Error.Validation(SupportErrorCodes.InvalidTarget, "Save the support file somewhere else than the log folder.");

        var temp = path + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            int logCount = 0, crashCount = 0;
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                await AddTextAsync(zip, "about.txt", About(), cancellationToken);
                foreach (var (info, _) in RollingLogWriter.LogFiles(writer.Folders.Logs).OrderBy(l => l.Info.Name, StringComparer.Ordinal))
                    if (await AddFileAsync(zip, "logs/" + info.Name, info.FullName, cancellationToken)) logCount++;
                foreach (var report in crashes.List())
                    if (await AddFileAsync(zip, "crash/" + report.FileName, Path.Combine(crashes.Folders.Crashes, report.FileName), cancellationToken)) crashCount++;
            }

            File.Move(temp, path, overwrite: true);
            var saved = new SupportFileSaved(path, logCount, crashCount, new FileInfo(path).Length);
            logger.LogInformation("Support file saved: {Logs} log file(s), {Crashes} crash report(s), {Bytes} bytes.", logCount, crashCount, saved.Bytes);
            await events.TryRecordAsync(BusinessEvent.Create(SupportCapabilities.Module, "support.file-saved", "support-file", null,
                $"A support file was saved with {logCount} log file(s) and {crashCount} crash report(s)."));
            return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The support file could not be saved.");
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return Error.Failure(SupportErrorCodes.Failed, "The support file could not be saved there. Choose another folder, for example the Desktop or a USB stick.");
        }
    }

    private string About()
    {
        var text = new StringBuilder()
            .AppendLine("GenericPOS support file")
            .AppendLine($"Saved:       {clock.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}")
            .AppendLine($"Application: {CrashReporter.ApplicationVersion()}")
            .AppendLine($"Windows:     {Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture})")
            .AppendLine($".NET:        {RuntimeInformation.FrameworkDescription}")
            .AppendLine($"Culture:     {CultureInfo.CurrentCulture.Name}, time zone {TimeZoneInfo.Local.Id}")
            .AppendLine($"License:     {licensing?.State.ToString() ?? "(not known)"}")
            .AppendLine("Modules:");
        foreach (var module in modules?.GetAll().Select(m => $"{m.Manifest.ModuleId.Value} {m.Manifest.Version}").Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>())
            text.AppendLine("  " + module);
        return LogRedactor.Redact(text.ToString());
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string content, CancellationToken cancellationToken)
    {
        await using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await entry.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
    }

    /// <summary>Copies one file (the log being written included: it is opened for shared reading). False when it disappeared meanwhile.</summary>
    private static async Task<bool> AddFileAsync(ZipArchive zip, string name, string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            await source.CopyToAsync(entry, cancellationToken);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>What the shell needs to say that the application closed unexpectedly (MISS-05): reports nobody has looked at yet.</summary>
public interface ICrashNoticeSource
{
    event EventHandler? Changed;

    IReadOnlyList<CrashReportInfo> UnseenCrashes();
}

// ---- the user-facing entry points: each checks support.export first

public sealed record GetSupportOverviewQuery;

public sealed class GetSupportOverviewQueryHandler(SupportService support, IAuthorizationService authorization)
{
    public async Task<Result<SupportOverview>> HandleAsync(GetSupportOverviewQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(SupportCapabilities.Export, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<SupportOverview>(allowed.Error);

        return support.GetOverview();
    }
}

public sealed record MarkCrashReportsSeenCommand;

public sealed class MarkCrashReportsSeenCommandHandler(SupportService support, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(MarkCrashReportsSeenCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(SupportCapabilities.Export, cancellationToken);
        if (allowed.IsFailure) return allowed;

        support.MarkCrashesSeen();
        return Result.Success();
    }
}

public sealed record SaveSupportFileCommand(string? Path);

public sealed class SaveSupportFileCommandHandler(SupportService support, IAuthorizationService authorization)
{
    public async Task<Result<SupportFileSaved>> HandleAsync(SaveSupportFileCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(SupportCapabilities.Export, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<SupportFileSaved>(allowed.Error);

        return await support.SaveSupportFileAsync(command.Path, cancellationToken);
    }
}
