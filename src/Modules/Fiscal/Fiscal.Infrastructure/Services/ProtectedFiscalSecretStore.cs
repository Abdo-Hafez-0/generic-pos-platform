using System.Text.Json;
using Fiscal.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Security;

namespace Fiscal.Infrastructure.Services;

/// <summary>
/// MISS-11c-3: the till's secrets (client secret, pre-shared key) in a file of their own next to the database (default
/// %LOCALAPPDATA%\GenericPOS\Fiscal\till-secrets.bin), protected for this Windows user by DPAPI through ISecretProtector. Never in the
/// database (so never in a backup) and never logged. Without a protector (not Windows) nothing is ever written.
/// </summary>
public sealed class ProtectedFiscalSecretStore(string path, ISecretProtector? protector = null, ILogger<ProtectedFiscalSecretStore>? logger = null)
    : IFiscalSecretStore
{
    private const string Purpose = "GenericPOS.Fiscal.TillSecrets.v1";
    private readonly ILogger _logger = logger ?? NullLogger<ProtectedFiscalSecretStore>.Instance;

    public string Path { get; } = path;

    public bool CanProtect => protector is not null;

    public Task<FiscalTillSecrets?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (protector is null || !File.Exists(Path)) return Task.FromResult<FiscalTillSecrets?>(null);
        if (!protector.TryUnprotect(File.ReadAllBytes(Path), Purpose, out var plain))
        {
            _logger.LogError("The till's secrets for the tax authority cannot be opened on this computer or by this Windows user; register the till again.");
            return Task.FromResult<FiscalTillSecrets?>(null);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(plain);
            // MISS-11d-3: Saudi Arabia keeps ZATCA's API secret alone (no pre-shared key); Egypt's registration requires its key itself
            return Task.FromResult(stored is { ClientSecret.Length: > 0 } ? new FiscalTillSecrets(stored.ClientSecret, stored.PresharedKey ?? string.Empty) : null);
        }
        catch (JsonException)
        {
            _logger.LogError("The till's secrets for the tax authority are unreadable; register the till again.");
            return Task.FromResult<FiscalTillSecrets?>(null);
        }
    }

    public Task WriteAsync(FiscalTillSecrets secrets, CancellationToken cancellationToken = default)
    {
        if (protector is null) throw new InvalidOperationException("No secret protector: the till's secrets cannot be stored.");
        var bytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new Stored(secrets.ClientSecret, secrets.PresharedKey)), Purpose);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, Path, overwrite: true);
        return Task.CompletedTask;
    }

    private sealed record Stored(string ClientSecret, string PresharedKey);
}
