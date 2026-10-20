using System.Windows.Input;
using Fiscal.Domain.Enums;
using Fiscal.UI.Resources;
using Fiscal.UI.ViewModels;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Presentation.Actions;
using UI.Tests.Pos;

namespace UI.Tests.Administration;

/// <summary>MISS-11a-1: the electronic receipts screen on the production-like offline desktop - the country, the shop's details and this till.</summary>
[Collection(nameof(RealDesktop))]
public sealed class FiscalOnRealDesktopTests
{
    private static async Task Run(FiscalViewModel vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task The_owner_switches_on_Egypts_receipts_with_the_shops_details_and_sees_this_till()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new FiscalViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));

        await vm.OnNavigatedToAsync();
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(FiscalCountry.None, vm.Country.Country);
        Assert.False(vm.NeedsDetails);
        Assert.Equal(FiscalText.StateOff, vm.IssuingText);
        Assert.Equal((Environment.MachineName, FiscalText.TillNotRegistered, "1"), (vm.TillName, vm.TillRegistrationText, vm.NextNumberText));
        Assert.True(vm.CanChooseCountry);
        Assert.True(vm.HasNoDocuments);                                                   // MISS-11a-3
        Assert.Equal(string.Empty, vm.AlertsText);
        Assert.False(vm.CanSendNow);                                                      // nothing is issued without a country part
        Assert.Equal(4, vm.Filters.Count);

        vm.Country = vm.Countries.Single(c => c.Country == FiscalCountry.Egypt);
        Assert.True(vm.NeedsDetails && vm.IsEgypt);
        (vm.TaxNumber, vm.BranchCode, vm.ActivityCode, vm.SellerNameArabic) = ("12345", "0", "4711", "متجر");
        await Run(vm, vm.SaveCommand);
        Assert.Equal("The tax registration number (RIN) is 9 digits.", vm.ErrorMessage);

        vm.TaxNumber = "123456789";
        await Run(vm, vm.SaveCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(FiscalText.Saved, vm.StatusMessage);
        Assert.Equal(string.Format(FiscalText.StateNoPart, vm.Countries.Single(c => c.Country == FiscalCountry.Egypt).Text), vm.IssuingText);

        var again = new FiscalViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));
        await again.OnNavigatedToAsync();
        Assert.Equal((FiscalCountry.Egypt, "123456789", "0", "4711", "متجر"), (again.Country.Country, again.TaxNumber, again.BranchCode, again.ActivityCode, again.SellerNameArabic));
    }
}
