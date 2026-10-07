using Platform.Presentation.Screens;
using Reporting.Application.Security;
using Reporting.UI.Resources;
using Reporting.UI.ViewModels;
using Reporting.UI.Views;

namespace Reporting.UI.Screens;

/// <summary>The screens the Reporting module offers to the desktop shell (FIX-01d).</summary>
public sealed class ReportingScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("reporting.overview", ReportingCapabilities.Module, ScreenGroups.Reports, () => ReportsText.ScreenTitle,
            typeof(BusinessOverviewView), typeof(BusinessOverviewViewModel), ReportingCapabilities.ViewReports, Order: 0),
    ];
}
