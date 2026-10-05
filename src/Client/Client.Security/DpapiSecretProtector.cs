using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Platform.Application.Abstractions.Security;

namespace Client.Security;

/// <summary>
/// <see cref="ISecretProtector"/> over Windows DPAPI, current-user scope. The purpose (plus a fixed application tag) is used as DPAPI's
/// optional entropy, so a blob only opens for the purpose it was made for. DPAPI itself authenticates the blob: any modification makes
/// it fail to open.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const string ApplicationTag = "GenericPOS.v1:";

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
        => ProtectedData.Protect(plaintext.ToArray(), Entropy(purpose), DataProtectionScope.CurrentUser);

    public bool TryUnprotect(ReadOnlySpan<byte> protectedData, string purpose, out byte[] plaintext)
    {
        try
        {
            plaintext = ProtectedData.Unprotect(protectedData.ToArray(), Entropy(purpose), DataProtectionScope.CurrentUser);
            return true;
        }
        catch (CryptographicException)
        {
            plaintext = [];
            return false;
        }
    }

    private static byte[] Entropy(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return Encoding.UTF8.GetBytes(ApplicationTag + purpose);
    }
}
