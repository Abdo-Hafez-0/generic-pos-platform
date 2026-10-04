namespace Platform.Core.Licensing;

/// <summary>
/// The licensing state of the installation, as evaluated LOCALLY from a signed license.
/// Each value is a distinct condition - they must never be conflated.
/// </summary>
public enum LicenseState
{
    /// <summary>No license has been activated on this installation.</summary>
    Unlicensed = 0,

    /// <summary>Signature verified, bound to this installation, inside the offline lease.</summary>
    Active = 1,

    /// <summary>The offline lease elapsed; inside the grace period. Renewal is required soon.</summary>
    GracePeriod = 2,

    /// <summary>The license (or its lease plus grace) has expired. Data is untouched; licensed access is restricted.</summary>
    Expired = 3,

    /// <summary>The server marked the license suspended (signed state).</summary>
    Suspended = 4,

    /// <summary>The server marked the license revoked (signed state).</summary>
    Revoked = 5,

    /// <summary>The stored license failed verification (bad signature, unknown key, wrong installation, malformed...).</summary>
    Invalid = 6
}
