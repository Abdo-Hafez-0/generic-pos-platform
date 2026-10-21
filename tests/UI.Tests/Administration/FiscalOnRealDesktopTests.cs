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
        Assert.False(vm.CanSendNow);                                                      // receipts are off: nothing is issued
        Assert.Equal(4, vm.Filters.Count);

        vm.Country = vm.Countries.Single(c => c.Country == FiscalCountry.Egypt);
        Assert.True(vm.NeedsDetails && vm.IsEgypt);
        (vm.TaxNumber, vm.BranchCode, vm.ActivityCode, vm.SellerNameArabic) = ("12345", "0", "4711", "متجر");
        await Run(vm, vm.SaveCommand);
        Assert.Equal("The tax registration number (RIN) is 9 digits.", vm.ErrorMessage);

        vm.TaxNumber = "123456789";
        await Run(vm, vm.SaveCommand);
        Assert.Equal("Choose the governorate of the shop's branch.", vm.ErrorMessage);   // MISS-11c: the branch address in the ETA's fields

        Assert.Equal(27, vm.Governorates.Count);
        (vm.Governorate, vm.City, vm.Street) = ("الجيزة", "الدقي", "شارع التحرير");
        await Run(vm, vm.SaveCommand);
        Assert.Equal("Give the branch's city, street and building number, as registered with the tax authority.", vm.ErrorMessage);

        (vm.BuildingNumber, vm.Floor) = ("7", "3");
        await Run(vm, vm.SaveCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(FiscalText.Saved, vm.StatusMessage);
        Assert.Matches("^MTJ-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", vm.DeviceSerial);
        Assert.Equal(string.Format(FiscalText.StateIssuing, vm.Countries.Single(c => c.Country == FiscalCountry.Egypt).Text), vm.IssuingText);   // MISS-11c: Egypt's part is installed
        Assert.True(vm.CanSendNow);

        var again = new FiscalViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));
        await again.OnNavigatedToAsync();
        Assert.Equal((FiscalCountry.Egypt, "123456789", "0", "4711", "متجر"), (again.Country.Country, again.TaxNumber, again.BranchCode, again.ActivityCode, again.SellerNameArabic));
        Assert.Equal(("الجيزة", "الدقي", "شارع التحرير", "7", "3", ""), (again.Governorate, again.City, again.Street, again.BuildingNumber, again.Floor, again.Room));
        Assert.Equal(vm.DeviceSerial, again.DeviceSerial);

        // MISS-11c-3 (owner's choice, drawn option B): the till's registration in its window, the summary in the box
        Assert.Equal(("–", "–", "–", false), (again.EnvironmentText, again.ClientIdText, again.LastSignInText, again.TestSignInCommand.CanExecute(null)));
        var window = again.CreateRegistration();
        Assert.Equal((again.DeviceSerial, false, false), (window.DeviceSerial, window.HasSecrets, window.EnvironmentLocked));
        Assert.StartsWith("Windows ", window.PosOsVersion);
        window.Environment = window.Environments.Single(e => e.Environment == FiscalEnvironment.PreProduction);
        (window.ClientId, window.PosModelFramework) = ("0a1b2c3d-client-9f", "1");
        Assert.False(await window.SaveAsync("", ""));
        Assert.Equal("Give the client secret from the tax authority's portal (up to 100 characters).", window.ErrorMessage);
        Assert.True(await window.SaveAsync("portal-secret", "portal-key"));   // kept by Windows (DPAPI) on this computer

        await again.RegisteredAsync();
        Assert.Equal((FiscalText.TillSaved, FiscalText.EnvironmentPreProduction, "0a1b…9f", FiscalText.TillRegistered),
            (again.StatusMessage, again.EnvironmentText, again.ClientIdText, again.TillRegistrationText));
        Assert.True(again.CreateRegistration().HasSecrets);

        // the shop's internet is down: the sign-in test says so and the box shows it was refused
        await Run(again, again.TestSignInCommand);
        Assert.Contains("could not be reached", again.ErrorMessage);
        Assert.EndsWith(FiscalText.SignInRefused, again.LastSignInText);
    }

    private static async Task RunPanel(FiscalCodesPanel panel, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && panel.IsBusy; i++) await Task.Delay(10);
        Assert.False(panel.IsBusy);
    }

    [Fact]
    public async Task For_Egypt_the_codes_box_lists_what_is_missing_suggests_EGS_codes_saves_them_and_exports_them()
    {
        // MISS-11b (owner's decisions: all codes on this screen; GS1 barcodes used as they are; EGS suggested from the SKU and exported)
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await Integration.Tests.FailureTestKit.CreateShopAsync(desktop.Services, salePrice: 2.5m, stock: 10m);
        var vm = new FiscalViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));
        await vm.OnNavigatedToAsync();
        Assert.False(vm.Codes.IsShown);                                                   // receipts off: no codes needed

        vm.Country = vm.Countries.Single(c => c.Country == FiscalCountry.Egypt);
        (vm.TaxNumber, vm.BranchCode, vm.ActivityCode, vm.SellerNameArabic) = ("123456789", "0", "4711", "متجر");
        (vm.Governorate, vm.City, vm.Street, vm.BuildingNumber) = ("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12");
        await Run(vm, vm.SaveCommand);
        Assert.True(vm.Codes.IsShown && vm.Codes.IsEgypt && vm.Codes.ShowsProducts);
        var row = Assert.Single(vm.Codes.Products);
        Assert.True(row.IsMissing);
        Assert.Equal(string.Format(FiscalText.CodesTabMissing, FiscalText.CodesProducts, 1), vm.Codes.ProductsTabText);

        await RunPanel(vm.Codes, vm.Codes.SuggestCommand);
        Assert.Equal((FiscalItemCodeType.EGS, "EG-123456789-" + shop.Sku.ToUpperInvariant()), (row.Type, row.Code));
        vm.Codes.Units.Single(u => u.Row.IsUsed).Code = "EA";
        Assert.True(vm.Codes.HasChanges);
        await RunPanel(vm.Codes, vm.Codes.SaveCommand);
        Assert.Null(vm.Codes.ErrorMessage);
        Assert.Equal(FiscalText.CodesSaved, vm.Codes.StatusMessage);
        Assert.False(Assert.Single(vm.Codes.Products).IsMissing);
        Assert.Equal(FiscalText.CodesProducts, vm.Codes.ProductsTabText);

        var file = Path.Combine(Path.GetTempPath(), $"egs-{Guid.NewGuid():N}.csv");
        try
        {
            await vm.Codes.ExportAsync(file);
            Assert.Contains("EG-123456789-" + shop.Sku.ToUpperInvariant(), await File.ReadAllTextAsync(file));
        }
        finally
        {
            File.Delete(file);
        }

        vm.Codes.ShowTaxRatesSection = true;
        Assert.True(vm.Codes.ShowsEgyptTaxRates && !vm.Codes.ShowsProducts);
    }

    [Fact]
    public void The_status_bar_names_the_missing_codes()
    {
        var text = Fiscal.UI.Screens.FiscalShellNotice.Describe(new Fiscal.Application.DTOs.FiscalAlertsDto(0, 0, 0, 0, 6, 3, 1, 2));
        Assert.Contains(string.Format(FiscalText.NoticeMissingProducts, 3), text);
        Assert.Contains(string.Format(FiscalText.NoticeMissingUnits, 1), text);
        Assert.Contains(string.Format(FiscalText.NoticeMissingTaxRates, 2), text);
    }
}
