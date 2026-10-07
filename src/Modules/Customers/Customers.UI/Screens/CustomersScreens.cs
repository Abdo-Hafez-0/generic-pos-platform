using Customers.Application.Security;
using Customers.UI.Resources;
using Customers.UI.ViewModels;
using Customers.UI.Views;
using Platform.Presentation.Screens;

namespace Customers.UI.Screens;

/// <summary>
/// The screens the Customers module offers to the desktop shell (FIX-01d). Shown to holders of customers.customer.view (reading personal
/// data; available in every license state); changes are authorized by the handlers (customers.customer.manage).
/// </summary>
public sealed class CustomersScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("customers.list", CustomersCapabilities.Module, ScreenGroups.People, () => CustomersText.ScreenTitle,
            typeof(CustomersView), typeof(CustomersViewModel), CustomersCapabilities.ViewCustomers, Order: 0),
    ];
}
