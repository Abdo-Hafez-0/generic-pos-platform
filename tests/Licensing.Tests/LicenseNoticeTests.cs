using Client.Licensing.Application;
using Client.Licensing.Domain;
using Platform.Core.Licensing;

namespace Licensing.Tests;

public sealed class LicenseNoticeTests
{
    private static LicenseEvaluation Evaluation(LicenseState state, InvalidReason reason = InvalidReason.None, ExpiryKind expiry = ExpiryKind.None)
        => new(state, null, reason, expiry, state is LicenseState.Active or LicenseState.GracePeriod, DateTimeOffset.UtcNow);

    [Fact]
    public void An_active_license_needs_no_notice()
        => Assert.Equal(LicenseNoticeLevel.None, LicenseNotice.Describe(Evaluation(LicenseState.Active)).Level);

    [Fact]
    public void The_grace_period_is_a_warning_not_a_restriction()
        => Assert.Equal(LicenseNoticeLevel.Warning, LicenseNotice.Describe(Evaluation(LicenseState.GracePeriod)).Level);

    [Fact]
    public void A_rolled_back_clock_tells_the_user_exactly_what_to_do()
    {
        var notice = LicenseNotice.Describe(Evaluation(LicenseState.Invalid, InvalidReason.ClockRollback));

        Assert.Equal(LicenseNoticeLevel.Restricted, notice.Level);
        Assert.Contains("clock", notice.Message);
        Assert.Contains("renew", notice.Message);
    }

    [Theory]
    [InlineData(LicenseState.Unlicensed, InvalidReason.None, ExpiryKind.None)]
    [InlineData(LicenseState.Expired, InvalidReason.None, ExpiryKind.LicenseExpired)]
    [InlineData(LicenseState.Expired, InvalidReason.None, ExpiryKind.LeaseExpired)]
    [InlineData(LicenseState.Suspended, InvalidReason.None, ExpiryKind.None)]
    [InlineData(LicenseState.Revoked, InvalidReason.None, ExpiryKind.None)]
    [InlineData(LicenseState.Invalid, InvalidReason.BadSignature, ExpiryKind.None)]
    [InlineData(LicenseState.Invalid, InvalidReason.WrongInstallation, ExpiryKind.None)]
    [InlineData(LicenseState.Invalid, InvalidReason.ClockRollback, ExpiryKind.None)]
    [InlineData(LicenseState.Invalid, InvalidReason.NotYetValid, ExpiryKind.None)]
    public void Every_restriction_is_explained_and_says_the_data_is_safe(LicenseState state, InvalidReason reason, ExpiryKind expiry)
    {
        var notice = LicenseNotice.Describe(Evaluation(state, reason, expiry));

        Assert.Equal(LicenseNoticeLevel.Restricted, notice.Level);
        Assert.Contains("data is safe", notice.Message);
    }
}
