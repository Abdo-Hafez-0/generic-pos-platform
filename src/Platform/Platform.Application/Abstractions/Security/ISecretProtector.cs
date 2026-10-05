namespace Platform.Application.Abstractions.Security;

/// <summary>
/// Protects small local secrets and integrity-sensitive state at rest (installation identity, the clock high-water mark, future
/// credentials) with whatever the operating system offers for the current user - never with a key stored next to the data.
/// The abstraction is technology-independent; the Windows implementation (DPAPI) lives in Client.Security.
///
/// A blob is bound to its <c>purpose</c>: something protected for one purpose cannot be opened as another, and a blob made by another
/// user or on another machine cannot be opened at all. That makes copying the file elsewhere, or editing it, detectable. It is a
/// raise-the-bar measure against accidental or casual tampering, not protection from someone who can run code as the same user.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose);

    /// <summary>False (and an empty result) when the data was not protected for this purpose by this user on this machine, or was altered.</summary>
    bool TryUnprotect(ReadOnlySpan<byte> protectedData, string purpose, out byte[] plaintext);
}
