using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Application.Abstractions.Hardware;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Tests.Common.Hardware;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// Stage 12 - every peripheral failing in every way (unplugged, timing out, a driver that throws) on the real desktop composition and the real
/// database. A device failure is a notice or a failed device result: it never changes, duplicates or rolls back a business transaction, and the next
/// operation works as soon as the device is back. The database is inspected after each failure.
/// </summary>
[Collection(HostCollection.Name)]
[Trait("Category", "Failure")]
public sealed class HardwareFailureTests
{
    private sealed class OtherDevicesModule(FakeLabelPrinter label, FakeScale scale, FakeBarcodeScanner scanner) : IHostingModule
    {
        public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        {
            services.AddSingleton<ILabelPrinter>(label);
            services.AddSingleton<IScale>(scale);
            services.AddSingleton<IBarcodeScanner>(scanner);
        }
    }

    public static IEnumerable<object[]> FailingModes() =>
    [
        [FakeMode.Unavailable, HardwareErrors.UnavailableCode],
        [FakeMode.Timeout, HardwareErrors.TimeoutCode],
        [FakeMode.Throws, HardwareErrors.FailedCode]
    ];

    // ------------------------------------------------------------------ receipt printer and cash drawer

    [Theory]
    [MemberData(nameof(FailingModes))]
    public async Task ReceiptPrinterAndDrawerFailure_DoesNotRollbackCompletedSale_AndTheNextSaleAndAReprintWorkOnceTheyAreBack(FakeMode mode, string expectedCode)
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        desktop.Printer.Mode = mode;
        desktop.Drawer.Mode = mode;
        var (_, firstCart) = await OpenCartAsync(desktop.Services, shop, 3m);

        var first = await CheckoutAsync(desktop.Services, firstCart);

        // the sale is complete and durable, whatever the devices did
        Assert.True(first.IsSuccess, first.ErrorMessage);
        var afterFirst = await BusinessState.ReadAsync(desktop.Host);
        Assert.Equal((1L, 1L, 1L, 1L, 2L, 1L), (afterFirst.Sales, afterFirst.SaleItems, afterFirst.SalesTransactions, afterFirst.Payments, afterFirst.StockMovements, afterFirst.CheckedOutCarts));
        Assert.Equal(7m, await OnHandAsync(desktop.Host, shop.ProductId));

        // the cashier is told, precisely, that the sale was saved and what did not happen
        Assert.Equal(2, first.HardwareNotices!.Count);
        var printerNotice = Assert.Single(first.HardwareNotices!, n => n.Device == "receipt printer");
        var drawerNotice = Assert.Single(first.HardwareNotices!, n => n.Device == "cash drawer");
        Assert.Equal(expectedCode, printerNotice.ErrorCode);
        Assert.Contains("The sale was completed and saved, but the receipt could not be printed", printerNotice.Message);
        Assert.Contains("The sale was completed and saved, but the cash drawer could not be opened", drawerNotice.Message);
        Assert.DoesNotContain("   at ", printerNotice.Message);
        Assert.Empty(desktop.Printer.Printed);
        Assert.Equal((1, 1), (desktop.Printer.Attempts, desktop.Drawer.Attempts));   // one attempt each: no hidden retry that could print twice

        // the devices come back: the next sale prints and kicks the drawer, and the first receipt can be reprinted - without a second sale
        desktop.Printer.Mode = FakeMode.Works;
        desktop.Drawer.Mode = FakeMode.Works;
        var (_, secondCart) = await OpenCartAsync(desktop.Services, shop, 1m);
        var second = await CheckoutAsync(desktop.Services, secondCart);
        Assert.True(second.IsSuccess, second.ErrorMessage);
        Assert.Empty(second.HardwareNotices!);
        Assert.Single(desktop.Printer.Printed);
        Assert.Equal(1, desktop.Drawer.Opened);

        using (var scope = desktop.Services.CreateScope())
        {
            var reprint = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().PrintReceiptAsync(firstCart);
            Assert.True(reprint.IsSuccess, reprint.ErrorMessage);
        }

        Assert.Equal(2, desktop.Printer.Printed.Count);
        Assert.Equal(3m, desktop.Printer.Printed[1].Lines.Single().Quantity);   // the reprint is of the FIRST sale
        var final = await BusinessState.ReadAsync(desktop.Host);
        Assert.Equal((2L, 2L, 3L), (final.Sales, final.Payments, final.StockMovements));   // reprinting created nothing
        Assert.Equal(6m, await OnHandAsync(desktop.Host, shop.ProductId));
    }

    [Fact]
    public async Task APrinterThatFailsOnlyAfterTheSale_NeverMakesTheSaleRepeatable()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        desktop.Printer.Mode = FakeMode.Throws;
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 1m);

        Assert.True((await CheckoutAsync(desktop.Services, cartId)).IsSuccess);
        var retry = await CheckoutAsync(desktop.Services, cartId);   // the cashier, unsure, presses pay again

        Assert.Equal("POS.Checkout.CartNotOpen", retry.ErrorCode);
        Assert.Equal(1, (await BusinessState.ReadAsync(desktop.Host)).Sales);
        Assert.Equal(9m, await OnHandAsync(desktop.Host, shop.ProductId));
    }

    // ------------------------------------------------------------------ label printer

    [Theory]
    [MemberData(nameof(FailingModes))]
    public async Task LabelPrinterFailure_IsAPlainFailure_ChangesNoBusinessData_AndRecovers(FakeMode mode, string expectedCode)
    {
        var label = new FakeLabelPrinter { Mode = mode };
        await using var desktop = await OfflineDesktop.StartAsync(extra: [new OtherDevicesModule(label, new FakeScale(), new FakeBarcodeScanner())]);
        var shop = await CreateShopAsync(desktop.Services);
        var before = await BusinessState.ReadAsync(desktop.Host);

        using var scope = desktop.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IPOSDevices>();
        var failed = await devices.PrintProductLabelAsync(shop.Sku, 2);

        Assert.False(failed.IsSuccess);
        Assert.Equal(expectedCode, failed.ErrorCode);
        Assert.DoesNotContain("   at ", failed.ErrorMessage);
        Assert.Equal(before, await BusinessState.ReadAsync(desktop.Host));
        Assert.Empty(label.Printed);

        label.Mode = FakeMode.Works;
        Assert.True((await devices.PrintProductLabelAsync(shop.Sku, 2)).IsSuccess);
        Assert.Single(label.Printed);
        Assert.Equal(before, await BusinessState.ReadAsync(desktop.Host));
    }

    // ------------------------------------------------------------------ scale

    [Theory]
    [MemberData(nameof(FailingModes))]
    public async Task ScaleFailure_IsAPlainFailure_AndRecovers(FakeMode mode, string expectedCode)
    {
        var scale = new FakeScale { Mode = mode };
        await using var desktop = await OfflineDesktop.StartAsync(extra: [new OtherDevicesModule(new FakeLabelPrinter(), scale, new FakeBarcodeScanner())]);
        using var scope = desktop.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IPOSDevices>();

        var failed = await devices.ReadWeightAsync();

        Assert.False(failed.IsSuccess);
        Assert.Equal(expectedCode, failed.ErrorCode);

        scale.Mode = FakeMode.Works;
        var recovered = await devices.ReadWeightAsync();
        Assert.True(recovered.IsSuccess, recovered.ErrorMessage);
        Assert.Equal(0.750m, recovered.Kilograms);
    }

    [Theory]
    [InlineData(-0.5, "Kilogram")]      // a negative weight
    [InlineData(5000, "Kilogram")]      // far above what any retail scale reports
    public async Task AnImplausibleScaleReading_IsRejected_NotUsed(double kilograms, string unit)
    {
        var scale = new FakeScale { Reading = new WeightReading((decimal)kilograms, Enum.Parse<WeightUnit>(unit), true, DateTimeOffset.UtcNow) };
        await using var desktop = await OfflineDesktop.StartAsync(extra: [new OtherDevicesModule(new FakeLabelPrinter(), scale, new FakeBarcodeScanner())]);
        using var scope = desktop.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().ReadWeightAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(HardwareErrors.InvalidDataCode, result.ErrorCode);
    }

    // ------------------------------------------------------------------ barcode scanner

    private static async Task<POSScanOutcome> ScanAsync(IPOSBarcodeInput input, FakeBarcodeScanner scanner, string code)
    {
        var done = new TaskCompletionSource<POSScanOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, POSScanOutcome outcome) { if (outcome.Barcode == code) done.TrySetResult(outcome); }
        input.ScanProcessed += Handler;
        try
        {
            scanner.Scan(code);
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            input.ScanProcessed -= Handler;
        }
    }

    [Theory]
    [InlineData("NO-SUCH-PRODUCT-12345")]                   // unknown
    [InlineData("   ")]                                     // blank
    [InlineData("\u0007\u0000\u001b[31mCOLA-1")]            // control characters from a noisy line
    [InlineData("'; DROP TABLE cat_Products; --")]          // hostile text
    public async Task AScannerReturningInvalidInput_IsRejectedWithoutTouchingTheCart(string code)
    {
        var scanner = new FakeBarcodeScanner();
        await using var desktop = await OfflineDesktop.StartAsync(extra: [new OtherDevicesModule(new FakeLabelPrinter(), new FakeScale(), scanner)]);
        var shop = await CreateShopAsync(desktop.Services);
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 1m);
        var input = desktop.Services.GetRequiredService<IPOSBarcodeInput>();
        input.BindCart(cartId);
        Assert.True((await input.StartAsync()).IsSuccess);
        var before = await BusinessState.ReadAsync(desktop.Host);

        var outcome = await ScanAsync(input, scanner, code);

        Assert.False(outcome.IsSuccess);
        Assert.StartsWith("POS.", outcome.ErrorCode);
        Assert.Equal(before, await BusinessState.ReadAsync(desktop.Host));
        Assert.Equal(1, await CountAsync(desktop.Host, "cat_Products"));   // the hostile text did nothing
        using (var scope = desktop.Services.CreateScope())
            Assert.Equal(1m, (await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetCartAsync(cartId))!.Items.Single().Quantity);

        // a good scan right after still works
        var good = await ScanAsync(input, scanner, shop.Sku);
        Assert.True(good.IsSuccess, good.ErrorMessage);
    }

    [Theory]
    [InlineData(FakeMode.Unavailable)]
    [InlineData(FakeMode.Timeout)]
    [InlineData(FakeMode.Throws)]
    public async Task ABarcodeScannerThatCannotStart_LeavesTheCashierTypingCodes_AndStartsOnceItIsBack(FakeMode mode)
    {
        var scanner = new FakeBarcodeScanner { Mode = mode };
        await using var desktop = await OfflineDesktop.StartAsync(extra: [new OtherDevicesModule(new FakeLabelPrinter(), new FakeScale(), scanner)]);
        var shop = await CreateShopAsync(desktop.Services);
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 1m);
        var input = desktop.Services.GetRequiredService<IPOSBarcodeInput>();
        input.BindCart(cartId);

        var started = await input.StartAsync();

        Assert.False(started.IsSuccess);
        Assert.DoesNotContain("   at ", started.ErrorMessage);

        // typing the code still works, and a sale is unaffected
        using (var scope = desktop.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IPOSService>().AddProductAsync(cartId, shop.Sku, 1m)).IsSuccess);
        Assert.True((await CheckoutAsync(desktop.Services, cartId)).IsSuccess);
        Assert.Equal(8m, await OnHandAsync(desktop.Host, shop.ProductId));

        scanner.Mode = FakeMode.Works;
        Assert.True((await input.StartAsync()).IsSuccess);
    }
}
