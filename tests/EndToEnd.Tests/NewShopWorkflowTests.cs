using System.Globalization;
using System.Windows.Automation;
using Xunit.Abstractions;
using static EndToEnd.Tests.DesktopApp;

namespace EndToEnd.Tests;

/// <summary>
/// MISS-06b (user decisions 2026-10-14): the business workflow on the REAL executable, set up entirely through the screens of a brand-new
/// installation: first run, activation against a real license server, category and unit, a product with a barcode, a warehouse, stock
/// received, the cash drawer shift opened; then the sale - the barcode scanned twice by a keyboard-wedge scanner, cash tendered, change
/// given, the receipt printed (ESC/POS to a file, decoded and checked) - the sale in Sales history, the stock reduced, and the shift closed
/// with the counted cash matching. Finally the application closes cleanly and its log holds no error.
/// Opt-in (GENERICPOS_E2E=1): it starts real processes and drives a real window. On failure a screenshot of the application's window is
/// kept next to the test output.
/// </summary>
public sealed class NewShopWorkflowTests(ITestOutputHelper output) : IDisposable
{
    private const string Password = "Sh0p!Secure-2026";
    private const string Barcode = "5449000000996";
    private const string StoreName = "E2E Corner Shop";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genericpos-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("GENERICPOS_E2E_KEEP") == "1") return;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Step(string text) => output.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");

    [E2EFact]
    public void A_new_shop_is_set_up_through_the_screens_sells_a_scanned_item_prints_the_receipt_and_closes_the_shift()
    {
        Directory.CreateDirectory(_root);
        using var server = new LicenseServerProcess(_root);
        var receipt = Path.Combine(_root, "receipt.bin");
        File.WriteAllBytes(receipt, []);
        var data = Path.Combine(_root, "pc");
        using var app = new DesktopApp(data, new Dictionary<string, string>
        {
            ["Database:DatabaseFolder"] = "Custom",
            ["Database:CustomFolderPath"] = data,
            ["Licensing:StorageDirectory"] = Path.Combine(data, "Licensing"),
            ["Licensing:ServerBaseUrl"] = server.BaseUrl,
            ["Licensing:TrustedKeys:0:KeyId"] = LicenseServerProcess.KeyId,
            ["Licensing:TrustedKeys:0:PublicKey"] = server.PublicKey,
            ["Updater:UpdateRoot"] = Path.Combine(data, "Updates"),
            ["Backup:LocalFolder"] = Path.Combine(data, "usb"),
            ["Hardware:Scanner:Type"] = "KeyboardWedge",
            ["Hardware:ReceiptPrinter:Type"] = "EscPosFile",
            ["Hardware:ReceiptPrinter:Path"] = receipt,
            ["PosReceipt:StoreName"] = StoreName,
        });

        AutomationElement? main = null;
        try
        {
            // ---- first run: the administrator
            var start = app.SignInWindow();
            SetText(start, "UsernameBox", "owner");
            SetText(start, "DisplayNameBox", "Shop Owner");
            TypeSecret(start, "NewPasswordBox", Password);
            TypeSecret(start, "ConfirmPasswordBox", Password);
            Click(start, "PrimaryButton");
            main = app.MainWindow();
            Step("First run done: " + Text(main, "SignedInText"));

            // ---- activation against the real license server
            Open(main, "License");
            SetText(main, "ActivationKeyBox", server.ActivationKey);
            Click(main, "ActivateButton");
            Step("License: " + WaitText(main, "LicenseStatusText", t => t.Contains("activated", StringComparison.OrdinalIgnoreCase), "activation", TimeSpan.FromSeconds(60)));

            // ---- catalog: category, unit, product with a barcode and a price (tax included)
            Open(main, "Categories and units");
            SetText(main, "CategoryNameBox", "Drinks");
            Click(main, "AddCategoryButton");
            WaitText(main, "CategoriesStatusText", t => t.Length > 0, "category added");
            SetText(main, "UnitNameBox", "Piece");
            SetText(main, "UnitAbbreviationBox", "pc");
            Click(main, "AddUnitButton");
            Until(() => Rows(main, "UnitList").Count > 0 || Text(main, "CategoriesStatusText").Contains("Piece") ? "ok" : null, "unit added", TimeSpan.FromSeconds(15));
            Step("Category and unit added.");

            Open(main, "Products");
            Click(main, "NewProductButton");
            SetText(main, "SkuBox", "COLA-330");
            SetText(main, "NameBox", "Cola 330ml");
            Choose(main, "CategoryBox", "Drinks");
            Choose(main, "UnitBox", "Piece");
            SetText(main, "SalePriceBox", "2.50");
            SetText(main, "CostPriceBox", "1.10");
            SetText(main, "BarcodeBox", Barcode);
            Click(main, "SaveButton");
            Step("Product: " + WaitText(main, "ProductsStatusText", t => t.Length > 0, "product saved"));
            Assert.Equal(string.Empty, Text(main, "ProductsErrorText"));

            // ---- a warehouse and the opening stock
            Open(main, "Warehouses");
            Click(main, "NewWarehouseButton");   // MISS-08c: the panel beside the list
            SetText(main, "WarehouseNameBox", "Main store");
            SetText(main, "WarehouseCodeBox", "MAIN");
            Click(main, "AddWarehouseButton");
            WaitText(main, "WarehousesStatusText", t => t.Length > 0, "warehouse added");

            Open(main, "Stock");
            Click(main, "NewReceiptButton");   // MISS-08c: the panel beside the list
            SetText(main, "ReceiveCodeBox", "COLA-330");
            Choose(main, "ReceiveWarehouseBox", "Main store");
            SetText(main, "ReceiveQuantityBox", "24");
            SetText(main, "ReceiveReferenceBox", "Opening stock");
            Click(main, "ReceiveButton");
            Step("Stock: " + WaitText(main, "StockStatusText", t => t.Length > 0, "stock received"));
            Assert.Equal(string.Empty, Text(main, "StockErrorText"));

            // ---- the cash drawer shift, float 50
            Open(main, "Cash drawer");
            SetText(main, "OpeningFloatBox", "50");
            Click(main, "OpenShiftButton");
            Step("Shift: " + WaitText(main, "BalanceText", t => t.Contains("50"), "shift opened"));

            // ---- the sale: two scans, cash 10, change 5
            Open(main, "Point of sale");
            Choose(main, "WarehouseBox", "Main store");
            Click(main, "OpenSessionButton");
            Get(main, "CartGrid");
            Scan(main, "CartGrid", Barcode);
            Scan(main, "CartGrid", Barcode);
            WaitText(main, "TotalText", t => t == Money(5m), "the cart total 5.00 after two scans");
            var line = Assert.Single(Rows(main, "CartGrid"));
            Step("Cart: " + string.Join(" | ", line));
            Assert.Contains("Cola 330ml", line);

            Choose(main, "PaymentMethodBox", "Cash");
            SetText(main, "PaymentAmountBox", "10");
            Click(main, "AddPaymentButton");
            WaitText(main, "ChangeDueText", t => t == Money(5m), "change 5.00");
            Assert.Equal(Money(0m), Text(main, "AmountDueText"));
            Click(main, "CheckoutButton");
            Step("Checkout: " + WaitText(main, "PosStatusText", t => t.Length > 0, "checkout done"));
            Assert.Equal(string.Empty, Text(main, "PosErrorText"));

            // ---- the receipt the printer received
            var (lines, cuts) = EscPosReader.Read(Until(() => File.ReadAllBytes(receipt) is { Length: > 0 } b ? b : null, "the receipt", TimeSpan.FromSeconds(15)));
            output.WriteLine("Receipt:" + Environment.NewLine + string.Join(Environment.NewLine, lines.Select(l => "  | " + l)));
            Assert.Contains(lines, l => l.Trim() == StoreName);
            Assert.Contains(lines, l => l.Trim() == "Cola 330ml");
            Assert.Contains(lines, l => l.Contains("2 x 2.50") && l.TrimEnd().EndsWith("5.00", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("TOTAL", StringComparison.Ordinal) && l.TrimEnd().EndsWith("5.00", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("Tendered", StringComparison.Ordinal) && l.TrimEnd().EndsWith("10.00", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("Change", StringComparison.Ordinal) && l.TrimEnd().EndsWith("5.00", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("Cashier", StringComparison.Ordinal));
            Assert.Equal(1, cuts);
            // the time on the shop's clock (not UTC: found by this test)
            var printed = DateTime.ParseExact(lines.Single(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$")), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Assert.True(Math.Abs((DateTime.Now - printed).TotalMinutes) < 3, $"The receipt says {printed:HH:mm}; the PC's clock says {DateTime.Now:HH:mm}.");
            Assert.DoesNotContain(lines, l => l.Contains("COPY"));

            // ---- the receipt printed again (MISS-07c): the last sale, then the sale chosen from the recent sales; each a copy with its payment
            IReadOnlyList<string> Reprinted(string button)
            {
                File.WriteAllBytes(receipt, []);   // the file printer writes from the start: empty it to read only the copy
                Click(main, button);
                Step("Reprint: " + WaitText(main, "PosStatusText", t => t.Contains("copy", StringComparison.OrdinalIgnoreCase), "the copy sent"));
                var (copy, _) = EscPosReader.Read(Until(() => File.ReadAllBytes(receipt) is { Length: > 0 } b ? b : null, "the copy", TimeSpan.FromSeconds(15)));
                Assert.Contains(copy, l => l.Trim() == "*** COPY ***");
                Assert.Contains(copy, l => l.StartsWith("TOTAL", StringComparison.Ordinal) && l.TrimEnd().EndsWith("5.00", StringComparison.Ordinal));
                Assert.Contains(copy, l => l.StartsWith("Tendered", StringComparison.Ordinal) && l.TrimEnd().EndsWith("10.00", StringComparison.Ordinal));
                Assert.Contains(copy, l => l.StartsWith("Change", StringComparison.Ordinal) && l.TrimEnd().EndsWith("5.00", StringComparison.Ordinal));
                return copy;
            }

            Reprinted("ReprintLastButton");
            Click(main, "RecentSalesButton");
            var recent = Until(() => Rows(main, "RecentGrid") is { Count: > 0 } r ? r : null, "the recent sales", TimeSpan.FromSeconds(15));
            Step("Recent sales: " + string.Join(" | ", recent[0]));
            Assert.Single(recent);
            Assert.Contains(recent[0], c => c == Money(5m));
            var chosen = Reprinted("ReprintSelectedButton");
            Assert.Contains(chosen, l => l.Contains(recent[0][1]));   // the receipt reference shown in the list
            Click(main, "HideRecentButton");

            // ---- the sale in Sales history, the stock reduced
            Open(main, "Sales history");
            Click(main, "ShowSalesButton");
            var sale = Until(() => Rows(main, "SalesGrid") is { Count: > 0 } r ? r : null, "the sale in Sales history", TimeSpan.FromSeconds(15));
            Step("Sales history: " + string.Join(" | ", sale[0]));
            Assert.Single(sale);
            Assert.Contains(sale[0], c => c == Money(5m));

            Open(main, "Stock");
            Click(main, "StockSearchButton");
            var stock = Until(() => Rows(main, "StockGrid") is { Count: > 0 } r ? r : null, "the stock list", TimeSpan.FromSeconds(15));
            Step("Stock after the sale: " + string.Join(" | ", stock[0]));
            Assert.Contains(stock[0], c => c is "22" or "22.00" or "22.000");

            // ---- closing the shift: 50 float + 5 kept from the sale, counted 55, no difference
            Open(main, "Cash drawer");
            WaitText(main, "BalanceText", t => t.Contains("55"), "balance 55");
            SetText(main, "CountedBox", "55");
            Click(main, "CloseShiftButton");
            Step("Shift closed: " + WaitText(main, "CashStatusText", t => t.Length > 0, "shift closed"));
            Assert.Equal(string.Empty, Text(main, "CashErrorText"));
            var shifts = Until(() => Rows(main, "ShiftGrid") is { Count: > 0 } r ? r : null, "the closed shift", TimeSpan.FromSeconds(15));
            Step("Shift row: " + string.Join(" | ", shifts[0]));

            // ---- the application closes cleanly; its log has no error
            app.Close(main);
            Assert.Equal(0, app.ExitCode);
            var logs = Directory.GetFiles(Path.Combine(data, "GenericPOS", "Logs"), "genericpos-*.log");
            var errors = logs.SelectMany(File.ReadLines).Where(l => l.Contains("[ERR]") || l.Contains("[CRT]")).ToList();
            Assert.True(errors.Count == 0, "The log holds errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
            Assert.DoesNotContain(logs.SelectMany(File.ReadLines), l => l.Contains(Password));
            Step($"Closed with exit code 0; {logs.Length} log file(s), no error, no password.");
        }
        catch
        {
            if (main is not null && !app.HasExited)
            {
                var shot = Path.Combine(AppContext.BaseDirectory, $"e2e-failure-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                Screenshot(main, shot);
                output.WriteLine("Screenshot of the application: " + shot);
            }

            output.WriteLine("License server output:" + Environment.NewLine + server.Output);
            throw;
        }
    }

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);
}
