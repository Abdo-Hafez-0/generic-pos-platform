namespace Client.Licensing.Domain;

/// <summary>
/// The identity of THIS installation: a random identifier generated once and persisted locally.
///
/// Deliberately NOT a hardware fingerprint: it does not depend on a single hardware identifier and does not
/// probe the machine. It is stable across restarts and application updates because it lives in a licensing
/// folder outside the application directory, and it needs no network access.
/// </summary>
public sealed record InstallationIdentity(Guid InstallationId, DateTimeOffset CreatedAt)
{
    public static InstallationIdentity CreateNew(DateTimeOffset now) => new(Guid.NewGuid(), now);

    public bool IsValid => InstallationId != Guid.Empty;
}
