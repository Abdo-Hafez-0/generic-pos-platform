using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;
using POS.Application.Devices;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Tests.Common.Hardware;

namespace POS.Tests.Application;

/// <summary>
/// Stage 10: the POS reaches peripherals ONLY through the Platform hardware abstractions, and no peripheral can corrupt a sale.
/// Every test here runs against fake hardware; the sale itself uses the same stubs and real POS database as the other POS tests.
/// </summary>
public sealed class PosHardwareTests
{
    private sealed class FakeSet
    {
        public FakeReceiptPrinter Receipt { get; } = new();
        public FakeCashDrawer Drawer { get; } = new();
        public FakeLabelPrinter Label { get; } = new();
        public FakeScale Scale { get; } = new();
        public FakeBarcodeScanner Scanner { get; } = new();

        public void Register(IServiceCollection s)
        {
            s.AddSingleton<IReceiptPrinter>(Receipt);
            s.AddSingleton<ICashDrawer>(Drawer);
            s.AddSingleton<ILabelPrinter>(Label);
            s.AddSingleton<IScale>(Scale);
            s.AddSingleton<IBarcodeScanner>(Scanner);
        }
    }

    private sealed class CancellingPrinter(CancellationTokenSource source) : IReceiptPrinter
    {
        public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(DeviceStatus.Ready());

        public Task<Result> PrintAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default)
        {
            source.Cancel();                         // the cashier gives up while the printer is busy
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result.Success());
        }
    }

    private static async Task<(PosTestDatabase Db, FakeSet Hw)> StartAsync(
        Pricing.Contracts.Interfaces.IPriceResolver? prices = null, Payments.Contracts.Interfaces.IPaymentService? payments = null, bool withHardware = true)
    {
        var hw = new FakeSet();
        var db = await PosTestDatabase.CreateAsync(prices, payments, withHardware ? hw.Register : null);
        return (db, hw);
    }

    private static async Task<(Guid CartId, Guid SessionId)> CartAsync(PosTestDatabase db, decimal qty = 2m, decimal price = 10m, string sku = "SKU-1", string name = "Cola", string? barcode = null)
    {
        var product = db.Catalog.Register(sku, name, price, barcode);
        db.Inventory.SetStock(product, 100m);
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await service.OpenSessionAsync("cashier-7", Guid.NewGuid());
        var cart = await service.StartCartAsync(session.SessionId);
        Assert.True((await service.AddProductAsync(cart.CartId, sku, qty)).IsSuccess);
        return (cart.CartId, session.SessionId);
    }

    private static async Task<POSCheckoutResult> CheckoutAsync(PosTestDatabase db, Guid cart, POSPaymentRequest? payment = null, CancellationToken ct = default)
    {
        using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cart, null, payment, ct);
    }

    private static async Task<POSCartResult?> ReadCartAsync(PosTestDatabase db, Guid cart)
    {
        using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetCartAsync(cart);
    }

    private static T Get<T>(PosTestDatabase db, Func<IServiceProvider, T> pick)
    {
        using var scope = db.CreateScope();
        return pick(scope.ServiceProvider);
    }

    /// <summary>The sale is valid: completed in Sales, stock issued, cart checked out and saved.</summary>
    private static async Task AssertSaleIntactAsync(PosTestDatabase db, Guid cart, POSCheckoutResult result)
    {
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains("complete", db.Sales.Calls);
        Assert.Single(db.Inventory.Issued);
        var saved = await ReadCartAsync(db, cart);
        Assert.Equal(result.SaleId, saved!.SaleId);
    }

    // ---- receipt -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Checkout_PrintsAPrinterIndependentReceipt_AfterTheSaleIsSaved()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        var (cart, _) = await CartAsync(db, qty: 2m, price: 10m);

        var result = await CheckoutAsync(db, cart);

        await AssertSaleIntactAsync(db, cart, result);
        Assert.Empty(result.HardwareNotices!);
        var receipt = Assert.Single(hw.Receipt.Printed);
        Assert.Equal("Test Store", receipt.StoreName);
        Assert.Equal("cashier-7", receipt.Cashier);
        Assert.Equal("S-" + result.SaleId.ToString("N")[..8].ToUpperInvariant(), receipt.Reference);
        var line = Assert.Single(receipt.Lines);
        Assert.Equal(("Cola", 2m, 10m, 20m), (line.Description, line.Quantity, line.UnitPrice, line.LineTotal));
        Assert.Equal(20m, receipt.Total);
        Assert.Null(receipt.Payment);
        Assert.Equal(["Thank you"], receipt.FooterLines);
    }

    [Fact]
    public async Task Checkout_WithACashPayment_PutsPaymentAndChangeOnTheReceipt()
    {
        var payments = new PosPaymentsStub();
        var (db, hw) = await StartAsync(payments: payments);
        await using var _ = db;
        var (cart, _) = await CartAsync(db, qty: 2m, price: 10m);

        var result = await CheckoutAsync(db, cart, new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 50m));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var payment = Assert.Single(hw.Receipt.Printed).Payment!;
        Assert.Equal(("Cash", 20m, 50m, 30m), (payment.Method, payment.Amount, payment.Tendered, payment.Change));
    }

    [Fact]
    public async Task Checkout_WithNoHardwareAtAll_JustWorks()
    {
        var (db, _) = await StartAsync(withHardware: false);
        await using var _ = db;
        var (cart, _) = await CartAsync(db);

        var result = await CheckoutAsync(db, cart);

        await AssertSaleIntactAsync(db, cart, result);
        Assert.Empty(result.HardwareNotices!);
    }

    [Theory]
    [InlineData(FakeMode.Unavailable, HardwareErrors.UnavailableCode)]
    [InlineData(FakeMode.Timeout, HardwareErrors.TimeoutCode)]
    [InlineData(FakeMode.Throws, HardwareErrors.FailedCode)]
    public async Task APrinterThatFails_NeverCorruptsTheCompletedSale_AndTheCashierIsTold(FakeMode mode, string code)
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        hw.Receipt.Mode = mode;
        var (cart, _) = await CartAsync(db);

        var result = await CheckoutAsync(db, cart);

        await AssertSaleIntactAsync(db, cart, result);
        var notice = Assert.Single(result.HardwareNotices!);
        Assert.Equal(("receipt printer", code), (notice.Device, notice.ErrorCode));
        Assert.Contains("sale was completed and saved", notice.Message);
        Assert.Equal(1, hw.Receipt.Attempts);
        Assert.Empty(hw.Receipt.Printed);
    }

    [Fact]
    public async Task APrinterThatIsNotConfigured_IsSkippedSilently()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        hw.Receipt.Mode = FakeMode.NotConfigured;
        var (cart, _) = await CartAsync(db);

        var result = await CheckoutAsync(db, cart);

        await AssertSaleIntactAsync(db, cart, result);
        Assert.Empty(result.HardwareNotices!);
    }

    [Fact]
    public async Task ACashierWhoCancelsDuringPrinting_StillGetsTheCompletedSale()
    {
        using var cts = new CancellationTokenSource();
        var db = await PosTestDatabase.CreateAsync(configureServices: s => s.AddSingleton<IReceiptPrinter>(new CancellingPrinter(cts)));
        await using var _ = db;
        var (cart, _) = await CartAsync(db);

        var result = await CheckoutAsync(db, cart, ct: cts.Token);

        await AssertSaleIntactAsync(db, cart, result);
        Assert.Empty(result.HardwareNotices!);
    }

    [Fact]
    public async Task AutoPrintOff_DoesNotTouchThePrinter()
    {
        var hw = new FakeSet();
        var db = await PosTestDatabase.CreateAsync(configureServices: s =>
        {
            hw.Register(s);
            s.AddSingleton(new PosReceiptOptions { AutoPrintReceipt = false, AutoOpenDrawerOnCashSale = false });
        });
        await using var _ = db;
        var (cart, _) = await CartAsync(db);

        var result = await CheckoutAsync(db, cart);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, hw.Receipt.Attempts);
    }

    [Fact]
    public async Task AFailedCheckout_PrintsNothingAndOpensNothing()
    {
        var (db, hw) = await StartAsync(payments: new PosPaymentsStub());
        await using var _ = db;
        var (cart, _) = await CartAsync(db);
        db.Sales.FailAt = "confirm";

        var result = await CheckoutAsync(db, cart, new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 100m));

        Assert.False(result.IsSuccess);
        Assert.Null(result.HardwareNotices);
        Assert.Equal(0, hw.Receipt.Attempts);
        Assert.Equal(0, hw.Drawer.Attempts);
    }

    // ---- cash drawer -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACashSale_OpensTheDrawer_ButCardAndUnpaidSalesDoNot()
    {
        var payments = new PosPaymentsStub();
        var (db, hw) = await StartAsync(payments: payments);
        await using var _ = db;

        var (cash, _) = await CartAsync(db, sku: "A");
        await CheckoutAsync(db, cash, new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 100m));
        var (card, _) = await CartAsync(db, sku: "B");
        await CheckoutAsync(db, card, new POSPaymentRequest(POSPaymentMethod.Card));
        var (none, _) = await CartAsync(db, sku: "C");
        await CheckoutAsync(db, none);

        Assert.Equal(1, hw.Drawer.Opened);
        Assert.Equal(1, hw.Drawer.Attempts);
    }

    [Theory]
    [InlineData(FakeMode.Unavailable, HardwareErrors.UnavailableCode)]
    [InlineData(FakeMode.Throws, HardwareErrors.FailedCode)]
    public async Task ADrawerThatFails_IsAHardwareNotice_NeverAMoneyProblem(FakeMode mode, string code)
    {
        var payments = new PosPaymentsStub();
        var (db, hw) = await StartAsync(payments: payments);
        await using var _ = db;
        hw.Drawer.Mode = mode;
        var (cart, _) = await CartAsync(db, qty: 2m, price: 10m);

        var result = await CheckoutAsync(db, cart, new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 50m));

        await AssertSaleIntactAsync(db, cart, result);
        Assert.Equal(30m, result.ChangeDue);
        Assert.Single(payments.Recorded);
        Assert.Empty(payments.Voided);
        var notice = Assert.Single(result.HardwareNotices!);
        Assert.Equal(("cash drawer", code), (notice.Device, notice.ErrorCode));
        Assert.Single(hw.Receipt.Printed);              // the printer was unaffected by the drawer
    }

    [Fact]
    public async Task EveryPeripheralFailing_StillLeavesAValidSale_WithOneNoticeEach()
    {
        var payments = new PosPaymentsStub();
        var (db, hw) = await StartAsync(payments: payments);
        await using var _ = db;
        hw.Receipt.Mode = FakeMode.Throws;
        hw.Drawer.Mode = FakeMode.Unavailable;
        var (cart, _) = await CartAsync(db);

        var result = await CheckoutAsync(db, cart, new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 100m));

        await AssertSaleIntactAsync(db, cart, result);
        Assert.Equal(["receipt printer", "cash drawer"], result.HardwareNotices!.Select(n => n.Device));
    }

    [Fact]
    public async Task OpeningTheDrawerManually_ReportsFailuresAsResults()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        using var scope = db.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IPOSDevices>();

        Assert.True((await devices.OpenCashDrawerAsync()).IsSuccess);
        hw.Drawer.Mode = FakeMode.Unavailable;
        Assert.Equal(HardwareErrors.UnavailableCode, (await devices.OpenCashDrawerAsync()).ErrorCode);
        hw.Drawer.Mode = FakeMode.Throws;
        Assert.Equal(HardwareErrors.FailedCode, (await devices.OpenCashDrawerAsync()).ErrorCode);
        Assert.Equal(1, hw.Drawer.Opened);
    }

    // ---- reprint -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARejectedReceipt_CanBeReprintedLater()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        hw.Receipt.Mode = FakeMode.Unavailable;
        var (cart, _) = await CartAsync(db);
        Assert.Single((await CheckoutAsync(db, cart)).HardwareNotices!);

        hw.Receipt.Mode = FakeMode.Works;
        using var scope = db.CreateScope();
        var reprint = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().PrintReceiptAsync(cart);

        Assert.True(reprint.IsSuccess, reprint.ErrorMessage);
        Assert.Equal(20m, Assert.Single(hw.Receipt.Printed).Total);
    }

    [Fact]
    public async Task Reprint_RejectsOpenAndUnknownCarts_AndReportsMissingOrBrokenPrinters()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        var (open, _) = await CartAsync(db);

        using (var scope = db.CreateScope())
        {
            var devices = scope.ServiceProvider.GetRequiredService<IPOSDevices>();
            Assert.Equal("POS.Receipt.NotCheckedOut", (await devices.PrintReceiptAsync(open)).ErrorCode);
            Assert.Equal("POS.Receipt.CartNotFound", (await devices.PrintReceiptAsync(Guid.NewGuid())).ErrorCode);
        }

        await CheckoutAsync(db, open);
        hw.Receipt.Mode = FakeMode.Throws;
        using (var scope = db.CreateScope())
            Assert.Equal(HardwareErrors.FailedCode, (await scope.ServiceProvider.GetRequiredService<IPOSDevices>().PrintReceiptAsync(open)).ErrorCode);

        var (bare, _) = await StartAsync(withHardware: false);
        await using var __ = bare;
        var (cart, _) = await CartAsync(bare);
        await CheckoutAsync(bare, cart);
        using var bareScope = bare.CreateScope();
        Assert.Equal(HardwareErrors.NotConfiguredCode, (await bareScope.ServiceProvider.GetRequiredService<IPOSDevices>().PrintReceiptAsync(cart)).ErrorCode);
    }

    // ---- labels ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALabelIsPrintedForABarcodeOrASku_WithTheProductNamePriceAndSku()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        db.Catalog.Register("SKU-9", "Orange Juice", 3.5m, barcode: "6001");
        using var scope = db.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IPOSDevices>();

        Assert.True((await devices.PrintProductLabelAsync("6001", 3)).IsSuccess);
        Assert.True((await devices.PrintProductLabelAsync("SKU-9")).IsSuccess);

        Assert.Equal([new LabelDocument("Orange Juice", "SKU-9", 3.5m, 3), new LabelDocument("Orange Juice", "SKU-9", 3.5m, 1)], hw.Label.Printed);
    }

    [Fact]
    public async Task LabelProblems_AreResults()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        db.Catalog.Register("SKU-9", "Orange Juice", 3.5m);
        using var scope = db.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IPOSDevices>();

        Assert.Equal("POS.Label.ProductNotFound", (await devices.PrintProductLabelAsync("nope")).ErrorCode);
        Assert.Equal("POS.Label.CodeRequired", (await devices.PrintProductLabelAsync("  ")).ErrorCode);

        hw.Label.Mode = FakeMode.Unavailable;
        Assert.Equal(HardwareErrors.UnavailableCode, (await devices.PrintProductLabelAsync("SKU-9")).ErrorCode);
        hw.Label.Mode = FakeMode.Throws;
        Assert.Equal(HardwareErrors.FailedCode, (await devices.PrintProductLabelAsync("SKU-9")).ErrorCode);

        var (bare, _) = await StartAsync(withHardware: false);
        await using var __ = bare;
        using var bareScope = bare.CreateScope();
        Assert.Equal(HardwareErrors.NotConfiguredCode, (await bareScope.ServiceProvider.GetRequiredService<IPOSDevices>().PrintProductLabelAsync("x")).ErrorCode);
    }

    // ---- scale -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWeightIsReadAsAMeaningfulMeasurement()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        hw.Scale.Reading = new WeightReading(750m, WeightUnit.Gram, true, DateTimeOffset.UtcNow);
        using var scope = db.CreateScope();

        var weight = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().ReadWeightAsync();

        Assert.True(weight.IsSuccess);
        Assert.Equal((750m, "Gram", true, 0.75m), (weight.Value, weight.Unit, weight.IsStable, weight.Kilograms));
    }

    [Fact]
    public async Task AnUnstableWeight_IsReportedAsUnstable_NotHidden()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        hw.Scale.Reading = new WeightReading(1.2m, WeightUnit.Kilogram, false, DateTimeOffset.UtcNow);
        using var scope = db.CreateScope();

        var weight = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().ReadWeightAsync();

        Assert.True(weight.IsSuccess);
        Assert.False(weight.IsStable);
    }

    [Theory]
    [InlineData(-1.0, WeightUnit.Kilogram)]
    [InlineData(1500.0, WeightUnit.Kilogram)]
    [InlineData(5_000_000.0, WeightUnit.Gram)]
    [InlineData(1.0, (WeightUnit)99)]
    public async Task AnInvalidScaleReading_IsRejected_NotUsed(double value, WeightUnit unit)
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        hw.Scale.Reading = new WeightReading((decimal)value, unit, true, DateTimeOffset.UtcNow);
        using var scope = db.CreateScope();

        var weight = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().ReadWeightAsync();

        Assert.False(weight.IsSuccess);
        Assert.Equal(HardwareErrors.InvalidDataCode, weight.ErrorCode);
    }

    [Theory]
    [InlineData(FakeMode.Unavailable, HardwareErrors.UnavailableCode)]
    [InlineData(FakeMode.Timeout, HardwareErrors.TimeoutCode)]
    [InlineData(FakeMode.Throws, HardwareErrors.FailedCode)]
    [InlineData(FakeMode.NotConfigured, HardwareErrors.NotConfiguredCode)]
    public async Task AScaleThatIsDown_IsAFailedResult(FakeMode mode, string code)
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        hw.Scale.Mode = mode;
        using var scope = db.CreateScope();

        var weight = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().ReadWeightAsync();

        Assert.False(weight.IsSuccess);
        Assert.Equal(code, weight.ErrorCode);
    }

    [Fact]
    public async Task WithNoScale_ReadingIsNotConfigured()
    {
        var (db, _) = await StartAsync(withHardware: false);
        await using var _ = db;
        using var scope = db.CreateScope();

        Assert.Equal(HardwareErrors.NotConfiguredCode, (await scope.ServiceProvider.GetRequiredService<IPOSDevices>().ReadWeightAsync()).ErrorCode);
    }

    // ---- status ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DeviceStatus_ReportsEveryDeviceKind_AndNeverThrows()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        hw.Receipt.Mode = FakeMode.Unavailable;
        hw.Label.Mode = FakeMode.NotConfigured;
        hw.Scale.Mode = FakeMode.Throws;
        using var scope = db.CreateScope();

        var status = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().GetDeviceStatusAsync();

        Assert.Equal(
            [("barcode scanner", "Ready"), ("receipt printer", "Unavailable"), ("label printer", "NotConfigured"), ("cash drawer", "Ready"), ("scale", "Unavailable")],
            status.Select(s => (s.Device, s.State)));
    }

    [Fact]
    public async Task DeviceStatus_WithNoHardware_IsAllNotConfigured()
    {
        var (db, _) = await StartAsync(withHardware: false);
        await using var _ = db;
        using var scope = db.CreateScope();

        var status = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().GetDeviceStatusAsync();

        Assert.Equal(5, status.Count);
        Assert.All(status, s => Assert.Equal("NotConfigured", s.State));
    }

    // ---- scanner -----------------------------------------------------------------------------------------------

    private static async Task<List<POSScanOutcome>> NextOutcomes(IPOSBarcodeInput input, int count, Action trigger)
    {
        var outcomes = new List<POSScanOutcome>();
        var done = new TaskCompletionSource();
        input.ScanProcessed += (_, o) =>
        {
            lock (outcomes) { outcomes.Add(o); if (outcomes.Count >= count) done.TrySetResult(); }
        };

        trigger();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return outcomes;
    }

    private static IPOSBarcodeInput Input(PosTestDatabase db)
    {
        using var scope = db.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IPOSBarcodeInput>();
    }

    [Fact]
    public async Task AScannedBarcode_IsAddedToTheBoundCart_LikeATypedCode()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        var (cart, _) = await CartAsync(db, qty: 1m, sku: "SKU-1", barcode: "BAR-1");
        var input = Input(db);
        input.BindCart(cart);
        Assert.True((await input.StartAsync()).IsSuccess);

        var outcomes = await NextOutcomes(input, 1, () => hw.Scanner.Scan("BAR-1"));

        Assert.True(outcomes[0].IsSuccess);
        Assert.Equal(2m, (await ReadCartAsync(db, cart))!.Items.Single().Quantity);
    }

    [Fact]
    public async Task RapidScans_AreProcessedOneAtATime_WithNoLostLines()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        var (cart, _) = await CartAsync(db, qty: 1m, barcode: "BAR-1");
        var input = Input(db);
        input.BindCart(cart);
        await input.StartAsync();

        var outcomes = await NextOutcomes(input, 5, () => { for (var i = 0; i < 5; i++) hw.Scanner.Scan("BAR-1"); });

        Assert.All(outcomes, o => Assert.True(o.IsSuccess, o.ErrorMessage));
        Assert.Equal(6m, (await ReadCartAsync(db, cart))!.Items.Single().Quantity);
    }

    [Fact]
    public async Task AnUnknownBarcode_IsRejectedWithAReason_AndTheCartIsUntouched()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        var (cart, _) = await CartAsync(db, qty: 1m);
        var input = Input(db);
        input.BindCart(cart);
        await input.StartAsync();

        var outcomes = await NextOutcomes(input, 1, () => hw.Scanner.Scan("NOPE"));

        Assert.False(outcomes[0].IsSuccess);
        Assert.False(string.IsNullOrEmpty(outcomes[0].ErrorCode));
        Assert.Equal(1m, (await ReadCartAsync(db, cart))!.Items.Single().Quantity);
    }

    [Fact]
    public async Task AScanWithNoCartBound_IsRejected()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        var input = Input(db);
        await input.StartAsync();

        var outcomes = await NextOutcomes(input, 1, () => hw.Scanner.Scan("BAR-1"));

        Assert.Equal("POS.Scan.NoActiveCart", outcomes[0].ErrorCode);
    }

    [Fact]
    public async Task ABrokenOutcomeSubscriber_DoesNotStopLaterScans()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        var (cart, _) = await CartAsync(db, qty: 1m, barcode: "BAR-1");
        var input = Input(db);
        input.BindCart(cart);
        input.ScanProcessed += (_, _) => throw new InvalidOperationException("UI exploded");
        await input.StartAsync();

        var outcomes = await NextOutcomes(input, 2, () => { hw.Scanner.Scan("BAR-1"); hw.Scanner.Scan("BAR-1"); });

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(3m, (await ReadCartAsync(db, cart))!.Items.Single().Quantity);
    }

    [Fact]
    public async Task StoppingTheInput_StopsScansFromReachingTheCart()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        var (cart, _) = await CartAsync(db, qty: 1m, barcode: "BAR-1");
        var input = Input(db);
        input.BindCart(cart);
        await input.StartAsync();
        Assert.Equal(1, hw.Scanner.SubscriberCount);

        await input.StopAsync();
        hw.Scanner.Scan("BAR-1");
        await Task.Delay(100);

        Assert.Equal(0, hw.Scanner.SubscriberCount);
        Assert.Equal(1m, (await ReadCartAsync(db, cart))!.Items.Single().Quantity);
    }

    [Theory]
    [InlineData(FakeMode.Unavailable, HardwareErrors.UnavailableCode)]
    [InlineData(FakeMode.Throws, HardwareErrors.FailedCode)]
    [InlineData(FakeMode.NotConfigured, HardwareErrors.NotConfiguredCode)]
    public async Task AScannerThatCannotStart_IsAResult_AndTypedCodesStillWork(FakeMode mode, string code)
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        hw.Scanner.Mode = mode;
        var (cart, _) = await CartAsync(db, qty: 1m);

        var started = await Input(db).StartAsync();

        Assert.False(started.IsSuccess);
        Assert.Equal(code, started.ErrorCode);
        using var scope = db.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IPOSService>().AddProductAsync(cart, "SKU-1", 1m)).IsSuccess);
        Assert.Equal(2m, (await ReadCartAsync(db, cart))!.Items.Single().Quantity);
    }

    [Fact]
    public async Task WithNoScanner_StartingIsNotConfigured_NotACrash()
    {
        var (db, _) = await StartAsync(withHardware: false);
        await using var _ = db;

        var started = await Input(db).StartAsync();

        Assert.Equal(HardwareErrors.NotConfiguredCode, started.ErrorCode);
        await Input(db).StopAsync();
    }

    [Fact]
    public async Task AScannerThatDiesAfterStart_LeavesAnExistingSaleAndCartIntact()
    {
        var (db, hw) = await StartAsync();
        await using var _ = db;
        var (cart, _) = await CartAsync(db, qty: 2m, barcode: "BAR-1");
        var input = Input(db);
        input.BindCart(cart);
        await input.StartAsync();

        hw.Scanner.Mode = FakeMode.Unavailable;       // unplugged: no more scans arrive
        var result = await CheckoutAsync(db, cart);

        await AssertSaleIntactAsync(db, cart, result);
    }

    /// <summary>Minimal Payments stub (the real module is not referenced by POS tests).</summary>
    private sealed class PosPaymentsStub : Payments.Contracts.Interfaces.IPaymentService
    {
        public List<Payments.Contracts.Models.RecordPaymentRequest> Recorded { get; } = [];
        public List<Guid> Voided { get; } = [];

        public Task<Payments.Contracts.Models.RecordPaymentResult> RecordPaymentAsync(Payments.Contracts.Models.RecordPaymentRequest request, CancellationToken cancellationToken = default)
        {
            Recorded.Add(request);
            return Task.FromResult(Payments.Contracts.Models.RecordPaymentResult.Success(Guid.NewGuid(), request.TenderedAmount is { } t ? t - request.Amount : 0m));
        }

        public Task<Payments.Contracts.Models.PaymentOperationResult> VoidPaymentAsync(Guid paymentId, string reason, CancellationToken cancellationToken = default)
        {
            Voided.Add(paymentId);
            return Task.FromResult(Payments.Contracts.Models.PaymentOperationResult.Success());
        }
    }
}
