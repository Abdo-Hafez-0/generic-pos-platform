using System.Security.Cryptography;
using System.Text;
using CloudBackup.Application;
using CloudBackup.Contracts;
using CloudBackup.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Backup;
using Platform.Application.Abstractions.Security;
using Platform.Core.Results;
using Security.BackupEnvelope;
using Tests.Common.Security;

namespace CloudBackup.Tests;

/// <summary>A reversible stand-in for DPAPI: bound to a "user" and a purpose, and it detects alteration (the Licensing.Tests pattern).</summary>
public sealed class FakeProtector(string user = "owner") : ISecretProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var key = Encoding.UTF8.GetBytes(user + "|" + purpose);
        var body = plaintext.ToArray().Select((b, i) => (byte)(b ^ key[i % key.Length] ^ 0x5A)).ToArray();
        return [.. SHA256.HashData([.. key, .. body])[..16], .. body];
    }

    public bool TryUnprotect(ReadOnlySpan<byte> protectedData, string purpose, out byte[] plaintext)
    {
        plaintext = [];
        var raw = protectedData.ToArray();
        if (raw.Length < 16) return false;
        var key = Encoding.UTF8.GetBytes(user + "|" + purpose);
        var body = raw[16..];
        if (!raw.AsSpan(0, 16).SequenceEqual(SHA256.HashData([.. key, .. body])[..16])) return false;
        plaintext = body.Select((b, i) => (byte)(b ^ key[i % key.Length] ^ 0x5A)).ToArray();
        return true;
    }
}

public sealed class FakeGate : IUnattendedWorkGate
{
    public bool Licensed { get; set; } = true;

    public bool IsLicensed(string capability) => capability == CloudBackupCapabilities.Manage ? Licensed : true;
}

public sealed class RecordingEvents : IBusinessEventSink
{
    public List<BusinessEvent> Events { get; } = [];

    public Task RecordAsync(BusinessEvent businessEvent, CancellationToken cancellationToken = default)
    {
        lock (Events) Events.Add(businessEvent);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> Actions() { lock (Events) return Events.Select(e => e.Action).ToList(); }
}

/// <summary>A backup server in memory: keeps what is uploaded, or fails the way it is told to.</summary>
public sealed class FakeBackupClient : IBackupClient
{
    public Dictionary<Guid, (byte[] Bytes, string Label)> Stored { get; } = [];
    public List<string> AccessUsed { get; } = [];
    public Error? FailWith { get; set; }
    public int Uploads { get; private set; }

    public async Task<Result<CloudBackupItem>> UploadAsync(string access, Stream content, string sha256, string label, string clientVersion, CancellationToken cancellationToken = default)
    {
        Uploads++;
        AccessUsed.Add(access);
        if (FailWith is { } error) return Result.Failure<CloudBackupItem>(error);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())) != sha256)
            return Result.Failure<CloudBackupItem>(Error.Validation(BackupClientErrorCodes.Failed, "hash mismatch"));
        var id = Guid.NewGuid();
        Stored[id] = (buffer.ToArray(), label);
        return new CloudBackupItem(id, DateTimeOffset.UtcNow, buffer.Length, sha256, label);
    }

    public Task<Result<IReadOnlyList<CloudBackupItem>>> ListAsync(string access, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Success<IReadOnlyList<CloudBackupItem>>(Stored.Select(s => new CloudBackupItem(s.Key, DateTimeOffset.UtcNow, s.Value.Bytes.Length, "", s.Value.Label)).ToList()));

    public Task<Result<Stream>> DownloadAsync(string access, Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Stored.TryGetValue(id, out var s) ? Result.Success<Stream>(new MemoryStream(s.Bytes)) : Result.Failure<Stream>(Error.NotFound(BackupClientErrorCodes.NotFound, "none")));

    public Task<Result> DeleteAsync(string access, Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Stored.Remove(id) ? Result.Success() : Result.Failure(Error.NotFound(BackupClientErrorCodes.NotFound, "none")));
}

public sealed class CountingSignal : IUploadSignal
{
    public int Raised { get; private set; }

    public void Raise() => Raised++;
}

/// <summary>The module on real files in a temporary folder, with a fake protector, a fake server and a vendor escrow key pair.</summary>
public sealed class CloudWorld : IDisposable
{
    public CloudWorld(bool canProtect = true, bool withEscrow = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "genericpos-cloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Workspace = new CloudBackupWorkspace(Path.Combine(Root, "CloudBackup"));
        Protector = canProtect ? new FakeProtector() : null;
        Escrow = withEscrow ? new EscrowPublicKey("escrow-2026", Convert.ToBase64String(Vendor.ExportSubjectPublicKeyInfo())) : null;
        Service = NewService();
    }

    public string Root { get; }
    public CloudBackupWorkspace Workspace { get; }
    public FakeProtector? Protector { get; }
    public ECDiffieHellman Vendor { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public EscrowPublicKey? Escrow { get; }
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.Zero));
    public FakeGate Gate { get; } = new();
    public FakeBackupClient Server { get; } = new();
    public RecordingEvents Events { get; } = new();
    public CountingSignal Signal { get; } = new();
    public CloudBackupService Service { get; private set; }

    public ProtectedKeyRing KeyRing => new(Workspace, NullLogger<ProtectedKeyRing>.Instance, Protector);

    /// <summary>A fresh service over the same files (an application restart).</summary>
    public CloudBackupService NewService()
    {
        Service = new CloudBackupService(
            KeyRing,
            new ProtectedAccessStore(Workspace, NullLogger<ProtectedAccessStore>.Instance, Protector),
            new JsonCloudOutbox(Workspace, NullLogger<JsonCloudOutbox>.Instance),
            new JsonCloudStatusStore(Workspace, NullLogger<JsonCloudStatusStore>.Instance),
            new ConfiguredEscrowKeys(Escrow is null ? [] : [Escrow]),
            Server, Gate, Clock, NullLogger<CloudBackupService>.Instance, Events, Signal);
        return Service;
    }

    /// <summary>Sets up the recovery code (typing back the right groups) and returns the code as written down.</summary>
    public async Task<string> SetUpCodeAsync()
    {
        var setup = Service.BeginRecoveryCode().Value;
        var groups = setup.Code.Split('-');
        var confirmed = await Service.ConfirmRecoveryCodeAsync(setup.SetupId, setup.GroupsToConfirm.Select(g => groups[g - 1]).ToList());
        Assert.True(confirmed.IsSuccess, confirmed.IsFailure ? confirmed.Error.Description : null);
        return setup.Code;
    }

    public const string AccessKey = "gpb_test-access-key-0123456789";

    public async Task ReadyAsync()
    {
        await SetUpCodeAsync();
        Assert.True((await Service.SetAccessAsync(AccessKey)).IsSuccess);
    }

    /// <summary>A plain "database" file standing in for the checked copy local backup hands over.</summary>
    public (string Path, BackupCopy Copy, byte[] Bytes) Snapshot(string name = "genericpos-20261010-230000.db", int size = 300_000)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes.AsSpan(size / 2));
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".db");
        File.WriteAllBytes(path, bytes);
        var copy = new BackupCopy(Guid.NewGuid(), name, Clock.GetUtcNow(), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "1.2.3", ["20261005_Init"], false);
        return (path, copy, bytes);
    }

    public string[] OutboxFiles() => Directory.Exists(Workspace.OutboxDirectory) ? Directory.GetFiles(Workspace.OutboxDirectory).Select(Path.GetFileName).OfType<string>().Order().ToArray() : [];

    public void Dispose()
    {
        Vendor.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
