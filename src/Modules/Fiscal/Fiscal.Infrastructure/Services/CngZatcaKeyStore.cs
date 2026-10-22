using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Fiscal.Saudi;

namespace Fiscal.Infrastructure.Services;

/// <summary>
/// MISS-11d-2 (owner's decisions: in the Windows key store, non-exportable, this Windows user): the till's ZATCA signing key - an ECDSA key
/// on secp256k1 made inside Windows CNG (Microsoft Software Key Storage Provider) with no export allowed, so the private key never leaves
/// it: never in a file, a backup or a log (ZATCA's Security Features Implementation Standards, requirement 6). One key per installation
/// (its name comes from the database's path); a reinstalled Windows loses it and the till is onboarded again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CngZatcaKeyStore(string keyName) : IZatcaKeyStore
{
    /// <summary>ZATCA's curve (as its SDK): Windows' name for secp256k1.</summary>
    public const string Curve = "secP256k1";

    public string KeyName { get; } = keyName;

    /// <summary>The key's name for the installation whose database is at <paramref name="databasePath"/>.</summary>
    public static string NameFor(string databasePath)
        => "Matjar-ZATCA-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(databasePath).ToUpperInvariant())))[..16];

    public bool Exists() => CngKey.Exists(KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);

    public ECDsa Create()
    {
        var parameters = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.None,                          // the private key can never be exported
            KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
            KeyUsage = CngKeyUsages.Signing,
        };
        parameters.Parameters.Add(new CngProperty("ECCCurveName", Encoding.Unicode.GetBytes(Curve + "\0"), CngPropertyOptions.None));
        return new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsa, KeyName, parameters));
    }

    public ECDsa? Open() => Exists() ? new ECDsaCng(CngKey.Open(KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)) : null;

    public void Delete()
    {
        if (!Exists()) return;
        using var key = CngKey.Open(KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        key.Delete();
    }
}
