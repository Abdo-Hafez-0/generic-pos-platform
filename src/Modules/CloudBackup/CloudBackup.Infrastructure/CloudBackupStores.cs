using System.Text.Json;
using System.Text.Json.Serialization;
using CloudBackup.Application;
using CloudBackup.Domain;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Security;
using Security.BackupEnvelope;

namespace CloudBackup.Infrastructure;

/// <summary>
/// The module's own folder, next to the database (default %LOCALAPPDATA%\GenericPOS\CloudBackup):
///
///   keyring.bin    the shop keys, protected for this Windows user (DPAPI through ISecretProtector); never the recovery code
///   access.bin     the vendor's backup access key, protected the same way
///   outbox\        encrypted copies waiting for upload (*.gpbak) and outbox.json (what each one is, retries)
///   status.json    the last upload, failure or reason for not copying
/// </summary>
public sealed class CloudBackupWorkspace(string root)
{
    public string Root { get; } = root;
    public string KeyRingPath => Path.Combine(Root, "keyring.bin");
    public string AccessPath => Path.Combine(Root, "access.bin");
    public string OutboxDirectory => Path.Combine(Root, "outbox");
    public string OutboxIndexPath => Path.Combine(OutboxDirectory, "outbox.json");
    public string StatusPath => Path.Combine(Root, "status.json");
}

internal static class CloudJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Default when missing; an unreadable file is kept aside (renamed, never deleted) and the default returned.</summary>
    internal static T Read<T>(string path, Func<T> fallback, ILogger logger)
    {
        if (!File.Exists(path)) return fallback();
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options) ?? fallback();
        }
        catch (JsonException ex)
        {
            var aside = $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
            logger.LogError(ex, "{Path} could not be read; it was kept as {Aside}.", path, aside);
            try { File.Move(path, aside); } catch (IOException) { }
            return fallback();
        }
    }
}

/// <summary>
/// The key ring, protected for this Windows user. Without a protector (not Windows) nothing is ever written in plain text: cloud backup
/// simply cannot be set up on that computer (Stage 11: consumers never invent a cipher of their own).
/// </summary>
public sealed class ProtectedKeyRing(CloudBackupWorkspace workspace, ILogger<ProtectedKeyRing> logger, ISecretProtector? protector = null) : IKeyRing
{
    private const string Purpose = "GenericPOS.CloudBackup.KeyRing.v1";
    private readonly SemaphoreSlim _lock = new(1, 1);

    private sealed record StoredKey(string KeyId, string Key, DateTimeOffset CreatedAt, bool Active);

    public bool CanProtect => protector is not null;

    public async Task<IReadOnlyList<CloudKeyInfo>> ListAsync(CancellationToken cancellationToken = default)
        => (await LoadAsync(cancellationToken)).Select(k => new CloudKeyInfo(k.KeyId, k.CreatedAt, k.Active)).ToList();

    public async Task<BackupKey?> GetActiveAsync(CancellationToken cancellationToken = default)
        => (await LoadAsync(cancellationToken)).Where(k => k.Active).Select(ToKey).FirstOrDefault();

    public async Task<BackupKey?> FindAsync(string keyId, CancellationToken cancellationToken = default)
        => (await LoadAsync(cancellationToken)).Where(k => k.KeyId == keyId).Select(ToKey).FirstOrDefault();

    public async Task AddActiveAsync(BackupKey key, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        if (protector is null) throw new InvalidOperationException("No secret protector: the key ring cannot be stored.");
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var keys = (await LoadAsync(cancellationToken)).Where(k => k.KeyId != key.KeyId).Select(k => k with { Active = false }).ToList();
            keys.Add(new StoredKey(key.KeyId, Convert.ToBase64String(key.Key), createdAt, true));
            CloudJson.WriteAtomically(workspace.KeyRingPath, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(keys, CloudJson.Options), Purpose));
        }
        finally
        {
            _lock.Release();
        }
    }

    private static BackupKey ToKey(StoredKey stored) => new(stored.KeyId, Convert.FromBase64String(stored.Key));

    private Task<List<StoredKey>> LoadAsync(CancellationToken cancellationToken)
    {
        if (protector is null || !File.Exists(workspace.KeyRingPath)) return Task.FromResult(new List<StoredKey>());
        if (!protector.TryUnprotect(File.ReadAllBytes(workspace.KeyRingPath), Purpose, out var plain))
        {
            // Another Windows user or another PC: these keys are of no use here. Kept as evidence, never silently replaced.
            logger.LogError("The cloud backup key ring cannot be opened on this computer or by this Windows user; set up the recovery code again.");
            return Task.FromResult(new List<StoredKey>());
        }

        return Task.FromResult(JsonSerializer.Deserialize<List<StoredKey>>(plain, CloudJson.Options) ?? []);
    }
}

public sealed class ProtectedAccessStore(CloudBackupWorkspace workspace, ILogger<ProtectedAccessStore> logger, ISecretProtector? protector = null) : ICloudAccessStore
{
    private const string Purpose = "GenericPOS.CloudBackup.Access.v1";

    public Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (protector is null || !File.Exists(workspace.AccessPath)) return Task.FromResult<string?>(null);
        if (protector.TryUnprotect(File.ReadAllBytes(workspace.AccessPath), Purpose, out var plain))
            return Task.FromResult<string?>(System.Text.Encoding.UTF8.GetString(plain));

        logger.LogError("The backup access key cannot be opened on this computer or by this Windows user; enter it again.");
        return Task.FromResult<string?>(null);
    }

    public Task WriteAsync(string? access, CancellationToken cancellationToken = default)
    {
        if (access is null)
        {
            if (File.Exists(workspace.AccessPath)) File.Delete(workspace.AccessPath);
            return Task.CompletedTask;
        }

        if (protector is null) throw new InvalidOperationException("No secret protector: the access key cannot be stored.");
        CloudJson.WriteAtomically(workspace.AccessPath, protector.Protect(System.Text.Encoding.UTF8.GetBytes(access), Purpose));
        return Task.CompletedTask;
    }
}

public sealed class JsonCloudOutbox(CloudBackupWorkspace workspace, ILogger<JsonCloudOutbox> logger) : ICloudOutbox
{
    public string Directory => workspace.OutboxDirectory;

    public Task<IReadOnlyList<OutboxEntry>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OutboxEntry>>(CloudJson.Read<List<OutboxEntry>>(workspace.OutboxIndexPath, () => [], logger));

    public Task SaveAsync(IReadOnlyList<OutboxEntry> entries, CancellationToken cancellationToken = default)
    {
        CloudJson.WriteAtomically(workspace.OutboxIndexPath, JsonSerializer.SerializeToUtf8Bytes(entries, CloudJson.Options));
        return Task.CompletedTask;
    }
}

public sealed class JsonCloudStatusStore(CloudBackupWorkspace workspace, ILogger<JsonCloudStatusStore> logger) : ICloudStatusStore
{
    public Task<CloudBackupStatus> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CloudJson.Read(workspace.StatusPath, () => new CloudBackupStatus(), logger));

    public Task WriteAsync(CloudBackupStatus status, CancellationToken cancellationToken = default)
    {
        CloudJson.WriteAtomically(workspace.StatusPath, JsonSerializer.SerializeToUtf8Bytes(status, CloudJson.Options));
        return Task.CompletedTask;
    }
}

/// <summary>The newest configured escrow key (CloudBackup:EscrowKeys); null when the vendor configured none (cloud backup is then not offered).</summary>
public sealed class ConfiguredEscrowKeys(IReadOnlyList<EscrowPublicKey> keys) : IEscrowKeySource
{
    public EscrowPublicKey? Current { get; } = keys.LastOrDefault(k => !string.IsNullOrWhiteSpace(k.KeyId) && !string.IsNullOrWhiteSpace(k.PublicKey));
}
