using Client.Licensing.Domain;
using Platform.Core.Licensing;

namespace Client.Licensing.Application;

/// <summary>How serious a license notice is for the person at the till.</summary>
public enum LicenseNoticeLevel
{
    /// <summary>Licensed; nothing to say.</summary>
    None = 0,

    /// <summary>Still working, but action is needed soon (grace period).</summary>
    Warning = 1,

    /// <summary>Licensed work is restricted until the stated action is taken. Data is never affected.</summary>
    Restricted = 2
}

public sealed record LicenseNoticeText(LicenseNoticeLevel Level, string Message);

/// <summary>
/// Turns a license evaluation into words a cashier or owner can act on - in particular the clock-rollback case, whose remedy (correct the
/// clock, or renew online) is not obvious from "Invalid". Every restricted message repeats that the data is safe.
/// </summary>
public static class LicenseNotice
{
    private const string DataSafe = " Your data is safe and remains readable.";

    public static LicenseNoticeText Describe(LicenseEvaluation evaluation) => evaluation.State switch
    {
        LicenseState.Active => new(LicenseNoticeLevel.None, string.Empty),
        LicenseState.GracePeriod => new(LicenseNoticeLevel.Warning,
            "The license has not been renewed for a while. Connect to the Internet and renew it soon to keep selling."),
        LicenseState.Unlicensed => new(LicenseNoticeLevel.Restricted,
            "This installation is not activated yet. An administrator can activate it with the activation key." + DataSafe),
        LicenseState.Expired when evaluation.ExpiryKind == ExpiryKind.LeaseExpired => new(LicenseNoticeLevel.Restricted,
            "The license could not be renewed in time. Connect to the Internet and renew it to continue selling." + DataSafe),
        LicenseState.Expired => new(LicenseNoticeLevel.Restricted,
            "The license has expired. Contact your vendor to extend it." + DataSafe),
        LicenseState.Suspended => new(LicenseNoticeLevel.Restricted,
            "The license is suspended by the vendor. Contact your vendor." + DataSafe),
        LicenseState.Revoked => new(LicenseNoticeLevel.Restricted,
            "The license has been revoked. Contact your vendor." + DataSafe),
        LicenseState.Invalid when evaluation.InvalidReason == InvalidReason.ClockRollback => new(LicenseNoticeLevel.Restricted,
            "The computer's clock is set earlier than the last time this program ran. Correct the date and time (or renew the license online) to continue selling." + DataSafe),
        LicenseState.Invalid when evaluation.InvalidReason == InvalidReason.WrongInstallation => new(LicenseNoticeLevel.Restricted,
            "The stored license belongs to another installation (for example after copying the program or a Windows profile change). Ask your vendor to release the license and activate this installation again." + DataSafe),
        LicenseState.Invalid when evaluation.InvalidReason == InvalidReason.NotYetValid => new(LicenseNoticeLevel.Restricted,
            "The license is not valid yet. Check the computer's date and time." + DataSafe),
        _ => new(LicenseNoticeLevel.Restricted,
            "The stored license could not be verified. An administrator can activate the installation again." + DataSafe)
    };
}
