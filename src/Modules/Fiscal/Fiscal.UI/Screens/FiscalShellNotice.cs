using System.Globalization;
using Fiscal.Application.Queries;
using Fiscal.Application.Security;
using Fiscal.UI.Resources;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;

namespace Fiscal.UI.Screens;

/// <summary>
/// MISS-11a-3 (owner's decision: warnings for managers): the status bar's warning about electronic receipts, for those who manage them -
/// documents that could not be made, refused by the authority, or waiting longer than the managers' limit.
/// </summary>
public sealed class FiscalShellNotice(IUiActionRunner runner) : IShellNoticeProvider
{
    public string RequiredCapability => FiscalCapabilities.Manage;

    public async Task<string?> GetNoticeAsync(CancellationToken cancellationToken = default)
    {
        var alerts = await runner.RunAsync((scope, ct) => scope.Get<GetFiscalAlertsQueryHandler>().HandleAsync(new GetFiscalAlertsQuery(), ct), cancellationToken);
        return alerts is { IsSuccess: true, Value: { NeedsAttention: true } a } ? Describe(a) : null;
    }

    /// <summary>"Electronic receipts: 2 could not be made, 1 refused by the tax authority, 4 waiting more than 6 hours".</summary>
    public static string Describe(Fiscal.Application.DTOs.FiscalAlertsDto alerts)
    {
        var parts = new List<string>();
        if (alerts.NotBuilt > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, FiscalText.NoticeNotBuilt, alerts.NotBuilt));
        if (alerts.Rejected > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, FiscalText.NoticeRejected, alerts.Rejected));
        if (alerts.OverLimit > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, FiscalText.NoticeOverdue, alerts.OverLimit, alerts.LimitHours));
        if (alerts.MissingProductCodes > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, FiscalText.NoticeMissingProducts, alerts.MissingProductCodes));   // MISS-11b
        if (alerts.MissingUnitCodes > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, FiscalText.NoticeMissingUnits, alerts.MissingUnitCodes));
        if (alerts.MissingTaxCodes > 0) parts.Add(string.Format(CultureInfo.CurrentCulture, FiscalText.NoticeMissingTaxRates, alerts.MissingTaxCodes));
        return FiscalText.NoticePrefix + " " + string.Join(FiscalText.ListSeparator, parts);
    }
}
