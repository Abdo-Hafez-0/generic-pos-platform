using Platform.Presentation.Screens;
using POS.Application.Security;
using POS.UI.Resources;
using POS.UI.ViewModels;
using POS.UI.Views;

namespace POS.UI.Screens;

/// <summary>The screens the POS module offers to the desktop shell (FIX-01b).</summary>
public sealed class PosScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor(
            Id: "pos.sell",
            Module: POSCapabilities.Module,
            Group: ScreenGroups.Sales,
            Title: () => PosText.ScreenTitle,
            ViewType: typeof(PosView),
            ViewModelType: typeof(PosViewModel),
            RequiredCapability: POSCapabilities.CreateSale,
            Order: 0),
    ];
}
