using Catalog.Application.Commands;
using Client.Hardware;
using Client.Host.Hosting;
using Inventory.Application.Commands;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Application.Abstractions.Hardware;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;
using Tests.Common.Hardware;

namespace Integration.Tests;

/// <summary>
/// Stage 10 on the REAL host and the REAL SQLite database: whatever the peripherals do, a completed sale is a completed,
/// durable sale. Hardware comes either from fakes registered like any hosting module or from the real adapters chosen by configuration.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class HardwareIsolationTests
{
    private sealed class FakeHardwareModule(FakeReceiptPrinter printer, FakeCashDrawer drawer, FakeBarcodeScanner scanner) : IHostingModule
    {
        public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        {
            services.AddSingleton<IReceiptPrinter>(printer);
            services.AddSingleton<ICashDrawer>(drawer);
            services.AddSingleton<IBarcodeScanner>(scanner);
        }
    }

    private sealed record Shop(Guid ProductId, Guid WarehouseId);

    private static async Task<Shop> CreateShopAsync(IServiceProvider services, decimal stock = 10m)
    {
        using var scope = services.CreateScope();
        var p = scope.ServiceProvider;
        var category = await p.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Drinks"));
        var unit = await p.GetRequiredService<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand("Piece", "pc"));
        var product = await p.GetRequiredService<CreateProductCommandHandler>()
            .HandleAsync(new CreateProductCommand("COLA-1", "Cola", category.Value.Value, unit.Value.Value, 2.5m, 1m));
        var warehouse = await p.GetRequiredService<CreateWarehouseCommandHandler>().HandleAsync(new CreateWarehouseCommand("Main", "MAIN"));
        var stocked = await p.GetRequiredService<AddStockCommandHandler>().HandleAsync(new AddStockCommand(product.Value.Value, warehouse.Value, stock));
        Assert.True(product.IsSuccess && warehouse.IsSuccess && stocked.IsSuccess);
        return new Shop(product.Value.Value, warehouse.Value);
    }

    private static async Task<Guid> CartWithAsync(IServiceProvider services, Shop shop, decimal quantity)
    {
        using var scope = services.CreateScope();
        var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await pos.OpenSessionAsync("cashier-1", shop.WarehouseId);
        var cart = await pos.StartCartAsync(session.SessionId);
        Assert.True((await pos.AddProductAsync(cart.CartId, "COLA-1", quantity)).IsSuccess);
        return cart.CartId;
    }

    private static async Task<POSCheckoutResult> CashCheckoutAsync(IServiceProvider services, Guid cartId)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPOSService>()
            .CheckoutAsync(cartId, payment: new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 20m));
    }

    private static async Task AssertDurableSaleAsync(IServiceProvider services, Shop shop, POSCheckoutResult checkout, decimal expectedOnHand)
    {
        Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
        using var scope = services.CreateScope();
        var sale = await scope.ServiceProvider.GetRequiredService<ISalesReader>().FindByIdAsync(checkout.SaleId);
        Assert.Equal(SaleStatusContract.Completed, sale!.Status);
        Assert.Equal(7.5m, sale.GrandTotal);
        var level = await scope.ServiceProvider.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId);
        Assert.Equal(expectedOnHand, level!.OnHand);
    }

    [Fact]
    public async Task WithWorkingFakeHardware_ARealCashSale_PrintsTheRealReceipt_AndKicksTheDrawer()
    {
        var printer = new FakeReceiptPrinter();
        var drawer = new FakeCashDrawer();
        await using var host = await IntegrationHost.StartAllAsync(extra: [new FakeHardwareModule(printer, drawer, new FakeBarcodeScanner())]);
        var shop = await CreateShopAsync(host.Services);
        var cart = await CartWithAsync(host.Services, shop, 3m);

        var checkout = await CashCheckoutAsync(host.Services, cart);

        await AssertDurableSaleAsync(host.Services, shop, checkout, 7m);
        Assert.Empty(checkout.HardwareNotices!);
        var receipt = Assert.Single(printer.Printed);
        var line = Assert.Single(receipt.Lines);
        Assert.Equal(("Cola", 3m, 2.5m, 7.5m), (line.Description, line.Quantity, line.UnitPrice, line.LineTotal));
        Assert.Equal(7.5m, receipt.Total);
        Assert.Equal(("Cash", 7.5m, 20m, 12.5m), (receipt.Payment!.Method, receipt.Payment.Amount, receipt.Payment.Tendered, receipt.Payment.Change));
        Assert.Equal(1, drawer.Opened);
    }

    [Fact]
    public async Task WhenEveryPeripheralFails_TheSaleIsStillCompleted_Persisted_AndSurvivesARestart()
    {
        var printer = new FakeReceiptPrinter { Mode = FakeMode.Throws };
        var drawer = new FakeCashDrawer { Mode = FakeMode.Unavailable };
        var folder = Path.Combine(Path.GetTempPath(), "genericpos-hw-" + Guid.NewGuid().ToString("N"));
        Shop shop;
        POSCheckoutResult checkout;

        try
        {
            await using (var host = await IntegrationHost.StartAsync(
                             [.. IntegrationHost.CoreModules, .. IntegrationHost.Stage8Modules], folder,
                             [new FakeHardwareModule(printer, drawer, new FakeBarcodeScanner())]))
            {
                host.KeepFiles = true;
                shop = await CreateShopAsync(host.Services);
                var cart = await CartWithAsync(host.Services, shop, 3m);

                checkout = await CashCheckoutAsync(host.Services, cart);

                await AssertDurableSaleAsync(host.Services, shop, checkout, 7m);
                Assert.Equal(["receipt printer", "cash drawer"], checkout.HardwareNotices!.Select(n => n.Device));
                Assert.Equal([HardwareErrors.FailedCode, HardwareErrors.UnavailableCode], checkout.HardwareNotices!.Select(n => n.ErrorCode));
            }

            // A fresh host on the same database: the sale is still there, completed, with the stock already reduced.
            await using var restarted = await IntegrationHost.StartAllAsync(reuseFolder: folder);
            await AssertDurableSaleAsync(restarted.Services, shop, checkout, 7m);
        }
        finally
        {
            IntegrationHost.DeleteFolder(folder);
        }
    }

    [Fact]
    public async Task WithNoHardwareModuleAtAll_CheckoutIsUnchanged()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var cart = await CartWithAsync(host.Services, shop, 3m);

        var checkout = await CashCheckoutAsync(host.Services, cart);

        await AssertDurableSaleAsync(host.Services, shop, checkout, 7m);
        Assert.Empty(checkout.HardwareNotices!);
    }

    [Fact]
    public async Task WithTheRealHardwareModuleAndNoConfiguration_EverythingIsNotConfigured_AndThePosWorks()
    {
        await using var host = await IntegrationHost.StartAllAsync(extra: [new HardwareHostingModule()]);
        var shop = await CreateShopAsync(host.Services);
        var cart = await CartWithAsync(host.Services, shop, 3m);

        var checkout = await CashCheckoutAsync(host.Services, cart);

        await AssertDurableSaleAsync(host.Services, shop, checkout, 7m);
        Assert.Empty(checkout.HardwareNotices!);
        using var scope = host.Services.CreateScope();
        var status = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().GetDeviceStatusAsync();
        Assert.All(status, s => Assert.Equal("NotConfigured", s.State));
    }

    [Fact]
    public async Task ARealEscPosPrinterOnADevicePath_ReceivesTheReceiptBytes()
    {
        var device = Path.Combine(Path.GetTempPath(), "genericpos-printer-" + Guid.NewGuid().ToString("N") + ".dev");
        await File.WriteAllBytesAsync(device, []);
        using var _ = Env(("Hardware__ReceiptPrinter__Type", "EscPosFile"), ("Hardware__ReceiptPrinter__Path", device));

        try
        {
            await using var host = await IntegrationHost.StartAllAsync(extra: [new HardwareHostingModule()]);
            var shop = await CreateShopAsync(host.Services);
            var cart = await CartWithAsync(host.Services, shop, 3m);

            var checkout = await CashCheckoutAsync(host.Services, cart);

            await AssertDurableSaleAsync(host.Services, shop, checkout, 7m);
            Assert.Empty(checkout.HardwareNotices!);
            var bytes = await File.ReadAllBytesAsync(device);
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes[..2]);
            Assert.Contains("Cola", text);
            Assert.Contains("7.50", text);
            Assert.Contains("12.50", text);                       // the change, from the real Payments module
            Assert.True(bytes.AsSpan().IndexOf(new byte[] { 0x1B, 0x70 }) < 0, "no drawer is configured here, so no kick pulse may be sent");
        }
        finally
        {
            File.Delete(device);
        }
    }

    [Fact]
    public async Task ARealPrinterThatIsOffline_DoesNotStopTheSale_AndTheCashierGetsAClearNotice()
    {
        var missing = Path.Combine(Path.GetTempPath(), "genericpos-no-printer-" + Guid.NewGuid().ToString("N") + ".dev");
        using var _ = Env(("Hardware__ReceiptPrinter__Type", "EscPosFile"), ("Hardware__ReceiptPrinter__Path", missing));

        await using var host = await IntegrationHost.StartAllAsync(extra: [new HardwareHostingModule()]);
        var shop = await CreateShopAsync(host.Services);
        var cart = await CartWithAsync(host.Services, shop, 3m);

        var checkout = await CashCheckoutAsync(host.Services, cart);

        await AssertDurableSaleAsync(host.Services, shop, checkout, 7m);
        var notice = Assert.Single(checkout.HardwareNotices!);
        Assert.Equal(("receipt printer", HardwareErrors.UnavailableCode), (notice.Device, notice.ErrorCode));
        Assert.Contains("sale was completed and saved", notice.Message);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public async Task AScannedBarcode_AddsToARealCart_AndAScannerThatDiesChangesNothing()
    {
        var scanner = new FakeBarcodeScanner();
        await using var host = await IntegrationHost.StartAllAsync(extra: [new FakeHardwareModule(new FakeReceiptPrinter(), new FakeCashDrawer(), scanner)]);
        var shop = await CreateShopAsync(host.Services);
        var cart = await CartWithAsync(host.Services, shop, 1m);
        var input = host.Services.GetRequiredService<IPOSBarcodeInput>();
        input.BindCart(cart);
        Assert.True((await input.StartAsync()).IsSuccess);

        var done = new TaskCompletionSource<POSScanOutcome>();
        input.ScanProcessed += (_, o) => done.TrySetResult(o);
        scanner.Scan("COLA-1");                                    // a SKU resolves exactly like a barcode
        var outcome = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(outcome.IsSuccess, outcome.ErrorMessage);
        using (var scope = host.Services.CreateScope())
            Assert.Equal(2m, (await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetCartAsync(cart))!.Items.Single().Quantity);

        scanner.Mode = FakeMode.Throws;                            // the scanner dies; the cart and checkout do not care
        var checkout = await CashCheckoutAsync(host.Services, cart);
        Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
    }

    private static IDisposable Env(params (string Key, string Value)[] values)
    {
        foreach (var (key, value) in values) Environment.SetEnvironmentVariable("GENERICPOS_" + key, value);
        return new EnvScope(values.Select(v => "GENERICPOS_" + v.Key).ToArray());
    }

    private sealed class EnvScope(string[] keys) : IDisposable
    {
        public void Dispose()
        {
            foreach (var key in keys) Environment.SetEnvironmentVariable(key, null);
        }
    }
}
