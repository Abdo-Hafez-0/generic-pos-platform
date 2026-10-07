using Audit.Application.Security;
using Audit.UI.Resources;
using Audit.UI.ViewModels;
using Audit.UI.Views;
using Platform.Presentation.Screens;

namespace Audit.UI.Screens;

/// <summary>The screens the Audit module offers to the desktop shell (FIX-01d).</summary>
public sealed class AuditScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("audit.log", AuditCapabilities.Module, ScreenGroups.Administration, () => AuditText.ScreenTitle,
            typeof(AuditLogView), typeof(AuditLogViewModel), AuditCapabilities.ViewAudit, Order: 80),
    ];
}
