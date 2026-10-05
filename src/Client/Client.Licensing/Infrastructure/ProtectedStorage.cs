using System.Globalization;
using System.Text;
using System.Text.Json;
using Client.Licensing.Application;
using Client.Licensing.Domain;
using Licensing.Contracts;
using Platform.Application.Abstractions.Security;

namespace Client.Licensing.Infrastructure;

/// <summary>
/// The on-disk form of locally protected state: one header line, then Base64 of the protected blob. Without a protector (a platform
/// that offers none) the state is written as plain text and the header is absent; nothing home-made is ever applied.
/// </summary>
internal static class ProtectedFile
{
    public const string Header = "GPOS-PROTECTED/1";

    public enum Kind
    {
        Missing,
        Plain,
        Protected,
        Unreadable
    }

    public static async Task WriteAsync(string path, byte[] plaintext, ISecretProtector? protector, string purpose, CancellationToken cancellationToken)
    {
        var content = protector is null
            ? Encoding.UTF8.GetString(plaintext)
            : Header + "\n" + Convert.ToBase64String(protector.Protect(plaintext, purpose));
        await AtomicFile.WriteAsync(path, content, cancellationToken);
    }

    public static async Task<(Kind Kind, byte[] Bytes)> ReadAsync(string path, ISecretProtector? protector, string purpose, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return (Kind.Missing, []);

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (IOException)
        {
            return (Kind.Unreadable, []);
        }

        if (!text.StartsWith(Header, StringComparison.Ordinal))
            return (Kind.Plain, Encoding.UTF8.GetBytes(text));

        if (protector is null) return (Kind.Unreadable, []);

        try
        {
            var blob = Convert.FromBase64String(text[Header.Length..].Trim());
            return protector.TryUnprotect(blob, purpose, out var plaintext) ? (Kind.Protected, plaintext) : (Kind.Unreadable, []);
        }
        catch (FormatException)
        {
            return (Kind.Unreadable, []);
        }
    }

    /// <summary>Keeps an unusable file as evidence for support (never silently overwritten), at most <paramref name="keep"/> copies.</summary>
    public static void PreserveEvidence(string path, DateTimeOffset now, int keep = 5)
    {
        try
        {
            var copy = $"{path}.unusable-{now.UtcDateTime:yyyyMMddHHmmssfff}";
            File.Copy(path, copy, overwrite: true);

            var directory = Path.GetDirectoryName(path)!;
            var old = Directory.GetFiles(directory, Path.GetFileName(path) + ".unusable-*").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep);
            foreach (var file in old)
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // keeping evidence is a courtesy; failing to must not stop the application.
        }
    }
}

/// <summary>
/// Stores the installation identity in <c>installation.json</c>, protected for the current user and machine when the platform offers
/// protection. A protected file that no longer opens (another user or machine, edited, corrupt) and a plain-text file where a protected one
/// is expected are NOT trusted: the file is kept as evidence, a security event is recorded, and the identity is regenerated - which breaks
/// the binding to the old license and so grants nothing (fail closed). Pre-Stage-11 plain-text identities are accepted and sealed only when
/// <c>Licensing:AllowLegacyPlaintextIdentity</c> is set, because accepting plain text for ever would let the file simply be copied to another PC.
/// </summary>
public sealed class FileInstallationIdentityStore(
    LicensingStorageOptions options,
    ISecretProtector? protector = null,
    ISecurityEventSink? events = null,
    TimeProvider? timeProvider = null,
    bool allowLegacyPlaintext = false) : IInstallationIdentityStore
{
    public const string Purpose = "licensing.installation-identity";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private string FilePath => Path.Combine(options.Directory, "installation.json");

    public async Task<InstallationIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var (kind, bytes) = await ProtectedFile.ReadAsync(FilePath, protector, Purpose, cancellationToken);
        switch (kind)
        {
            case ProtectedFile.Kind.Missing:
                return null;

            case ProtectedFile.Kind.Unreadable:
                await Unusable("the protected identity file could not be opened (another user or machine, or it was altered)", cancellationToken);
                return null;

            case ProtectedFile.Kind.Plain when protector is not null && !allowLegacyPlaintext:
                await Unusable("an unprotected identity file was found where a protected one is expected", cancellationToken);
                return null;
        }

        var identity = Parse(bytes);
        if (identity is null)
        {
            await Unusable("the identity file is malformed", cancellationToken);
            return null;
        }

        if (kind == ProtectedFile.Kind.Plain && protector is not null)
        {
            await SaveAsync(identity, cancellationToken);   // seal it
            await events.TryRecordAsync(SecurityEvent.Create(
                "security.installation.identity-sealed", SecurityEventOutcome.Success, subjectType: "installation", subjectId: identity.InstallationId.ToString(),
                summary: "a legacy unprotected installation identity was migrated to protected storage", occurredAt: _time.GetUtcNow()), cancellationToken);
        }

        return identity;
    }

    public Task SaveAsync(InstallationIdentity identity, CancellationToken cancellationToken = default)
        => ProtectedFile.WriteAsync(FilePath, JsonSerializer.SerializeToUtf8Bytes(identity, LicenseSerializer.Options), protector, Purpose, cancellationToken);

    private static InstallationIdentity? Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<InstallationIdentity>(bytes, LicenseSerializer.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task Unusable(string why, CancellationToken cancellationToken)
    {
        ProtectedFile.PreserveEvidence(FilePath, _time.GetUtcNow());
        await events.TryRecordAsync(SecurityEvent.Create(
            "security.installation.identity-unusable", SecurityEventOutcome.Denied, subjectType: "installation",
            summary: why + "; a new identity will be created and the old file was kept for support", occurredAt: _time.GetUtcNow()), cancellationToken);
    }
}

/// <summary>Stores the clock high-water mark (<c>clock.state</c>), protected like the identity when the platform offers protection.</summary>
public sealed class FileClockStateStore(
    LicensingStorageOptions options,
    ISecretProtector? protector = null,
    ISecurityEventSink? events = null,
    TimeProvider? timeProvider = null) : IClockStateStore
{
    public const string Purpose = "licensing.clock-high-water";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private string FilePath => Path.Combine(options.Directory, "clock.state");

    public async Task<DateTimeOffset?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var (kind, bytes) = await ProtectedFile.ReadAsync(FilePath, protector, Purpose, cancellationToken);
        if (kind == ProtectedFile.Kind.Missing) return null;

        // A plain-text mark is only legitimate when there is no protector; with one, the file must be a protected one.
        var usable = kind == ProtectedFile.Kind.Protected || (kind == ProtectedFile.Kind.Plain && protector is null);
        if (usable && DateTimeOffset.TryParse(Encoding.UTF8.GetString(bytes).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            return value;

        ProtectedFile.PreserveEvidence(FilePath, _time.GetUtcNow());
        await events.TryRecordAsync(SecurityEvent.Create(
            "security.license.clock-state-unusable", SecurityEventOutcome.Denied, subjectType: "license",
            summary: "the stored clock mark could not be trusted; the signed issue time of the license is used as the floor instead", occurredAt: _time.GetUtcNow()), cancellationToken);
        return null;
    }

    public Task SaveAsync(DateTimeOffset highWater, CancellationToken cancellationToken = default)
        => ProtectedFile.WriteAsync(FilePath, Encoding.UTF8.GetBytes(highWater.ToString("O", CultureInfo.InvariantCulture)), protector, Purpose, cancellationToken);
}
