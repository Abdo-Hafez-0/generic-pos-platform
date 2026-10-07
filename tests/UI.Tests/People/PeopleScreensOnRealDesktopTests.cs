using System.Globalization;
using System.Windows.Input;
using Customers.UI.Resources;
using Customers.UI.ViewModels;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Suppliers.UI.Resources;
using Suppliers.UI.ViewModels;
using UI.Tests.Pos;

namespace UI.Tests.People;

/// <summary>FIX-01d: the customers and suppliers screens on the production-like offline desktop.</summary>
[Collection(nameof(RealDesktop))]
public sealed class PeopleScreensOnRealDesktopTests
{
    private static UiActionRunner Runner(IServiceProvider services)
        => new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);

    private static async Task Run(ViewModelBase vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_customer_is_created_found_edited_deactivated_and_reactivated()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new CustomersViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();
        Assert.Equal(CustomersText.NoResults, vm.ResultInfo);

        await Run(vm, vm.NewCommand);
        Assert.False(vm.SaveCommand.CanExecute(null));   // code and name are required
        (vm.EditCode, vm.EditName, vm.EditEmail, vm.EditPhone, vm.EditNotes) = ("C-001", "Corner Cafe", "cafe@example.test", "0100 000 0000", "Pays monthly");
        await Run(vm, vm.SaveCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsEditorOpen);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, CustomersText.Created, "Corner Cafe"), vm.StatusMessage);

        vm.SearchText = "cafe@";
        await Run(vm, vm.SearchCommand);
        vm.Selected = Assert.Single(vm.Customers);

        await Run(vm, vm.EditCommand);
        Assert.Equal(("C-001", "Pays monthly"), (vm.EditCode, vm.EditNotes));   // the full record, not just the list item
        Assert.False(vm.IsNew);
        vm.EditName = "Corner Cafe Ltd";
        await Run(vm, vm.SaveCommand);
        Assert.Equal("Corner Cafe Ltd", vm.Customers.Single().Customer.Name);

        vm.Selected = vm.Customers[0];
        await Run(vm, vm.DeactivateCommand);
        vm.SearchText = string.Empty;
        await Run(vm, vm.SearchCommand);
        Assert.Empty(vm.Customers);

        vm.ShowInactive = true;
        await Run(vm, vm.SearchCommand);
        vm.Selected = Assert.Single(vm.Customers);
        Assert.Equal(CustomersText.Inactive, vm.Selected.StatusText);
        await Run(vm, vm.ReactivateCommand);
        Assert.Equal(CustomersText.Active, vm.Customers.Single().StatusText);
    }

    [Fact]
    public async Task A_duplicate_customer_code_is_refused_in_plain_words_and_the_editor_stays_open()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new CustomersViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();
        foreach (var name in new[] { "First", "Second" })
        {
            await Run(vm, vm.NewCommand);
            (vm.EditCode, vm.EditName) = ("C-001", name);
            await Run(vm, vm.SaveCommand);
        }

        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain("Exception", vm.ErrorMessage);
        Assert.True(vm.IsEditorOpen);
        await Run(vm, vm.CancelCommand);
        Assert.Single(vm.Customers);
    }

    [Fact]
    public async Task A_supplier_is_created_edited_and_deactivated()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new SuppliersViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();

        await Run(vm, vm.NewCommand);
        (vm.EditCode, vm.EditName, vm.EditPhone) = ("S-001", "Fresh Water Co", "0200 000 0000");
        await Run(vm, vm.SaveCommand);
        Assert.Null(vm.ErrorMessage);
        vm.Selected = Assert.Single(vm.Suppliers);

        await Run(vm, vm.EditCommand);
        vm.EditEmail = "orders@freshwater.test";
        await Run(vm, vm.SaveCommand);
        Assert.Equal("orders@freshwater.test", vm.Suppliers.Single().Supplier.Email);

        vm.Selected = vm.Suppliers[0];
        await Run(vm, vm.DeactivateCommand);
        Assert.Empty(vm.Suppliers);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, SuppliersText.Deactivated, "Fresh Water Co"), vm.StatusMessage);
    }
}
