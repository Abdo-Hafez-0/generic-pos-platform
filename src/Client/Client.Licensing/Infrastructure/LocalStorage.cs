using System.Text.Json;
using Client.Licensing.Application;
using Client.Licensing.Domain;
using Licensing.Contracts;

namespace Client.Licensing.Infrastructure;

/// <summary>Where licensing files live. Separate from the business database and from the application directory.</summary>
public sealed record LicensingStorageOptions(string Directory)
{
    /// <summary>%LOCALAPPDATA%\GenericPOS\Licensing - survives application updates, never touched by business modules.</summary>
    public static LicensingStorageOptions Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenericPOS", "Licensing"));
}

internal static class AtomicFile
{
    public static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Stores the installation identity as JSON (installation.json). Tampering with it cannot forge a license: the
/// identity is only compared against the INSTALLATION ID inside the signed license, so a changed identity makes the
/// stored license fail the binding check.
/// </summary>
public sealed class FileInstallationIdentityStore(LicensingStorageOptions options) : IInstallationIdentityStore
{
    private string FilePath => Path.Combine(options.Directory, "installation.json");

    public async Task<InstallationIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(FilePath, cancellationToken);
            return JsonSerializer.Deserialize<InstallationIdentity>(json, LicenseSerializer.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public Task SaveAsync(InstallationIdentity identity, CancellationToken cancellationToken = default)
        => AtomicFile.WriteAsync(FilePath, JsonSerializer.Serialize(identity, LicenseSerializer.Options), cancellationToken);
}

/// <summary>
/// Stores the signed license as JSON (license.json). Integrity/authenticity come from the signature, which is
/// verified on every load: any edit to the file is detected and the license is rejected. The license is not secret,
/// so no encryption is applied. Local storage cannot make a client tamper-proof; it only guarantees edits are detected.
/// </summary>
public sealed class FileLicenseStore(LicensingStorageOptions options) : ILicenseStore
{
    private string FilePath => Path.Combine(options.Directory, "license.json");

    public async Task<StoredLicense?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return null;

        string json;
        try
        {
            json = await File.ReadAllTextAsync(FilePath, cancellationToken);
        }
        catch (IOException)
        {
            return new StoredLicense(null);
        }

        return new StoredLicense(LicenseSerializer.TryDeserialize(json));
    }

    public Task SaveAsync(SignedLicense license, CancellationToken cancellationToken = default)
        => AtomicFile.WriteAsync(FilePath, LicenseSerializer.Serialize(license), cancellationToken);
}
