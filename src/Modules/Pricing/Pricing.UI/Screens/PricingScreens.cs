using Platform.Presentation.Screens;
using Pricing.Application.Security;
using Pricing.UI.Resources;
using Pricing.UI.ViewModels;
using Pricing.UI.Views;

namespace Pricing.UI.Screens;

/// <summary>The screens the Pricing module offers to the desktop shell (FIX-01d).</summary>
public sealed class PricingScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("pricing.prices", PricingCapabilities.Module, ScreenGroups.Inventory, () => PricingText.ScreenTitle,
            typeof(PricesView), typeof(PricesViewModel), PricingCapabilities.ManagePrices, Order: 5),
    ];
}
