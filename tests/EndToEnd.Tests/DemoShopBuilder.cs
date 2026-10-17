using System.Globalization;
using System.Text;
using System.Windows.Automation;
using Xunit.Abstractions;
using static EndToEnd.Tests.DesktopApp;

namespace EndToEnd.Tests;

/// <summary>Runs only with GENERICPOS_DEMO=1: it builds the demo shop (it is a tool, not a check).</summary>
public sealed class DemoFactAttribute : FactAttribute
{
    public DemoFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GENERICPOS_DEMO") != "1")
            Skip = "Builds the demo shop on the real executable: set GENERICPOS_DEMO=1 to run it.";
        Timeout = 0;
    }
}

/// <summary>
/// A demo shop for trying the application by hand (owner's request, 2026-10-15). Everything is entered through the real screens of a
/// brand-new installation, exactly as a shop would: the administrator, activation against a local license server (a 30-day demo license
/// for every module), tax, categories, 13 products with barcodes (one with an Arabic name), two warehouses, opening stock, customers,
/// suppliers, the cash drawer shift and three sales (cash with change, card, and one for a customer).
/// The shop lives in %LOCALAPPDATA%\MatjarDemo (never the real installation's data); "Start Matjar demo.cmd" there opens it again.
/// GENERICPOS_DEMO_RESET=1 replaces an existing demo shop.
/// </summary>
public sealed class DemoShopBuilder(ITestOutputHelper output)
{
    public const string Password = "Matjar!Demo-2026";
    private const string StoreName = "Matjar Demo Shop";

    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MatjarDemo");

    private sealed record Product(string Sku, string Name, string Category, decimal Price, decimal Cost, string Barcode, int Stock);

    private static readonly Product[] Products =
    [
        new("COLA-330", "Cola 330ml", "Drinks", 2.50m, 1.10m, "5449000000996", 48),
        new("WATER-500", "Water 500ml", "Drinks", 1.00m, 0.35m, Ean("629100000001"), 96),
        new("JUICE-1L", "Orange juice 1L", "Drinks", 4.75m, 2.60m, Ean("629100000002"), 24),
        new("COFFEE-250", "Ground coffee 250g", "Pantry", 12.90m, 7.80m, Ean("629100000003"), 18),
        new("TEA-100", "Black tea, 100 bags", "Pantry", 6.40m, 3.90m, Ean("629100000004"), 20),
        new("DATES-500", "تمر سكري 500 جم", "Pantry", 9.50m, 6.00m, Ean("629100000005"), 30),
        new("CHIPS-150", "Potato chips 150g", "Snacks", 3.25m, 1.70m, Ean("629100000006"), 40),
        new("CHOC-50", "Chocolate bar 50g", "Snacks", 1.80m, 0.90m, Ean("629100000007"), 60),
        new("BISC-200", "Butter biscuits 200g", "Snacks", 2.95m, 1.50m, Ean("629100000008"), 35),
        new("MILK-1L", "Fresh milk 1L", "Dairy", 3.60m, 2.40m, Ean("629100000009"), 24),
        new("YOG-500", "Plain yogurt 500g", "Dairy", 2.80m, 1.60m, Ean("629100000010"), 20),
        new("SOAP-750", "Dish soap 750ml", "Household", 5.20m, 2.90m, Ean("629100000011"), 15),
        new("TISS-200", "Tissues, 200 sheets", "Household", 2.10m, 1.00m, Ean("629100000012"), 30),
    ];

    /// <summary>An EAN-13 from its first 12 digits (the check digit added), so a real scanner would read the printed label the same way.</summary>
    private static string Ean(string twelve)
    {
        var sum = twelve.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return twelve + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
    }

    private void Step(string text) => output.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");

    /// <summary>A step that may fail without spoiling the rest of the shop (it is reported).</summary>
    private void Try(string what, Action action)
    {
        try { action(); Step("OK   " + what); }
        catch (Exception ex) { Step($"FAIL {what}: {ex.Message}"); }
    }

    /// <summary>Ticks a check box (TogglePattern; its IsChecked binding carries the choice).</summary>
    private static void Toggle(AutomationElement root, string automationId)
    {
        var toggle = (TogglePattern)Get(root, automationId).GetCurrentPattern(TogglePattern.Pattern);
        if (toggle.Current.ToggleState != ToggleState.On) toggle.Toggle();
    }

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);

    [DemoFact]
    public void Build_the_demo_shop()
    {
        if (Directory.Exists(Root))
        {
            if (Environment.GetEnvironmentVariable("GENERICPOS_DEMO_RESET") != "1")
                throw new InvalidOperationException($"A demo shop already exists in {Root}. Set GENERICPOS_DEMO_RESET=1 to replace it.");
            Directory.Delete(Root, recursive: true);
        }

        Directory.CreateDirectory(Root);
        var data = Path.Combine(Root, "shop");
        var receipts = Path.Combine(Root, "printed-receipts.bin");
        File.WriteAllBytes(receipts, []);

        using var server = new LicenseServerProcess(Path.Combine(Root, "license-server"));
        var settings = new Dictionary<string, string>
        {
            ["Database:DatabaseFolder"] = "Custom",
            ["Database:CustomFolderPath"] = data,
            ["Licensing:StorageDirectory"] = Path.Combine(data, "Licensing"),
            ["Licensing:TrustedKeys:0:KeyId"] = LicenseServerProcess.KeyId,
            ["Licensing:TrustedKeys:0:PublicKey"] = server.PublicKey,
            ["Updater:UpdateRoot"] = Path.Combine(data, "Updates"),
            ["Backup:LocalFolder"] = Path.Combine(Root, "backups"),
            ["Hardware:Scanner:Type"] = "KeyboardWedge",
            ["Hardware:ReceiptPrinter:Type"] = "EscPosFile",
            ["Hardware:ReceiptPrinter:Path"] = receipts,
            ["PosReceipt:StoreName"] = StoreName,
        };
        using var app = new DesktopApp(data, new Dictionary<string, string>(settings) { ["Licensing:ServerBaseUrl"] = server.BaseUrl });

        // ---- first run and activation
        var start = app.SignInWindow();
        SetText(start, "UsernameBox", "owner");
        SetText(start, "DisplayNameBox", "Shop Owner");
        TypeSecret(start, "NewPasswordBox", Password);
        TypeSecret(start, "ConfirmPasswordBox", Password);
        Click(start, "PrimaryButton");
        var main = app.MainWindow();
        Open(main, "License");
        SetText(main, "ActivationKeyBox", server.ActivationKey);
        Click(main, "ActivateButton");
        Step("License: " + WaitText(main, "LicenseStatusText", t => t.Contains("activated", StringComparison.OrdinalIgnoreCase), "activation", TimeSpan.FromSeconds(60)));

        // ---- tax (prices include it)
        Try("tax rate VAT 14% (default)", () =>
        {
            Open(main, "Tax rates");
            Click(main, "NewTaxRateButton");   // MISS-08c: the panel beside the list
            SetText(main, "TaxCodeBox", "VAT");
            SetText(main, "TaxNameBox", "VAT 14%");
            SetText(main, "TaxPercentBox", "14");
            Toggle(main, "TaxDefaultBox");
            Click(main, "AddTaxRateButton");
            WaitText(main, "TaxStatusText", t => t.Length > 0, "tax rate added");
        });

        // ---- categories and the unit
        Open(main, "Categories and units");
        foreach (var category in Products.Select(p => p.Category).Distinct())
            Try("category " + category, () =>
            {
                SetText(main, "CategoryNameBox", category);
                Click(main, "AddCategoryButton");
                WaitText(main, "CategoriesStatusText", t => t.Contains(category, StringComparison.Ordinal), "category " + category);
            });
        Try("unit Piece", () =>
        {
            SetText(main, "UnitNameBox", "Piece");
            SetText(main, "UnitAbbreviationBox", "pc");
            Click(main, "AddUnitButton");
            Until(() => Rows(main, "UnitList").Count > 0 || Text(main, "CategoriesStatusText").Contains("Piece") ? "ok" : null, "unit added", TimeSpan.FromSeconds(15));
        });

        // ---- products
        Open(main, "Products");
        foreach (var p in Products)
            Try($"product {p.Sku} {p.Name}", () =>
            {
                Click(main, "NewProductButton");
                SetText(main, "SkuBox", p.Sku);
                SetText(main, "NameBox", p.Name);
                Choose(main, "CategoryBox", p.Category);
                Choose(main, "UnitBox", "Piece");
                SetText(main, "SalePriceBox", p.Price.ToString("0.00", CultureInfo.CurrentCulture));
                SetText(main, "CostPriceBox", p.Cost.ToString("0.00", CultureInfo.CurrentCulture));
                SetText(main, "BarcodeBox", p.Barcode);
                Click(main, "SaveButton");
                Until(() => Text(main, "ProductsStatusText").Contains(p.Sku, StringComparison.Ordinal) || Text(main, "ProductsStatusText").Contains(p.Name, StringComparison.Ordinal)
                    || Text(main, "ProductsErrorText").Length > 0 ? "done" : null, "product saved", TimeSpan.FromSeconds(15));
                if (Text(main, "ProductsErrorText") is { Length: > 0 } error) throw new InvalidOperationException(error);
            });

        // ---- warehouses and the opening stock
        Open(main, "Warehouses");
        foreach (var (name, code) in new[] { ("Main store", "MAIN"), ("Back room", "BACK") })
            Try("warehouse " + name, () =>
            {
                Click(main, "NewWarehouseButton");   // MISS-08c: the panel beside the list
                SetText(main, "WarehouseNameBox", name);
                SetText(main, "WarehouseCodeBox", code);
                Click(main, "AddWarehouseButton");
                WaitText(main, "WarehousesStatusText", t => t.Contains(name, StringComparison.Ordinal) || t.Contains(code, StringComparison.Ordinal), "warehouse " + name);
            });
        Open(main, "Stock");
        foreach (var p in Products)
            Try($"stock {p.Sku} x{p.Stock}", () =>
            {
                Click(main, "NewReceiptButton");   // MISS-08c: the panel beside the list
                SetText(main, "ReceiveCodeBox", p.Sku);
                Choose(main, "ReceiveWarehouseBox", "Main store");
                SetText(main, "ReceiveQuantityBox", p.Stock.ToString(CultureInfo.InvariantCulture));
                SetText(main, "ReceiveReferenceBox", "Opening stock");
                Click(main, "ReceiveButton");
                Until(() => Text(main, "StockStatusText").Contains(p.Sku, StringComparison.Ordinal) || Text(main, "StockStatusText").Contains(p.Name, StringComparison.Ordinal)
                    || Text(main, "StockErrorText").Length > 0 ? "done" : null, "stock received", TimeSpan.FromSeconds(15));
                if (Text(main, "StockErrorText") is { Length: > 0 } error) throw new InvalidOperationException(error);
            });

        // ---- customers and suppliers
        Open(main, "Customers");
        foreach (var (code, name, phone) in new[] { ("C001", "Sara Ahmed", "0100 123 4567"), ("C002", "Omar Hassan", "0111 222 3333"), ("C003", "Nour Cafe", "0122 555 8899") })
            Try("customer " + name, () =>
            {
                Click(main, "NewCustomerButton");
                SetText(main, "CustomerCodeBox", code);
                SetText(main, "CustomerNameBox", name);
                SetText(main, "CustomerPhoneBox", phone);
                Click(main, "SaveCustomerButton");
                WaitText(main, "CustomersStatusText", t => t.Contains(name, StringComparison.Ordinal) || t.Contains(code, StringComparison.Ordinal), "customer " + name);
            });
        Open(main, "Suppliers");
        foreach (var (code, name, phone) in new[] { ("S001", "Delta Beverages", "02 2345 6789"), ("S002", "Nile Foods Trading", "02 3456 7890") })
            Try("supplier " + name, () =>
            {
                Click(main, "NewSupplierButton");
                SetText(main, "SupplierCodeBox", code);
                SetText(main, "SupplierNameBox", name);
                SetText(main, "SupplierPhoneBox", phone);
                Click(main, "SaveSupplierButton");
                WaitText(main, "SuppliersStatusText", t => t.Contains(name, StringComparison.Ordinal) || t.Contains(code, StringComparison.Ordinal), "supplier " + name);
            });

        // ---- the cash drawer shift and three sales
        Try("cash drawer shift, float 100", () =>
        {
            Open(main, "Cash drawer");
            SetText(main, "OpeningFloatBox", "100");
            Click(main, "OpenShiftButton");
            WaitText(main, "BalanceText", t => t.Contains("100"), "shift opened");
        });

        Open(main, "Point of sale");
        Try("open the till", () =>
        {
            Choose(main, "WarehouseBox", "Main store");
            Click(main, "OpenSessionButton");
            Get(main, "CartGrid");
        });

        void Sell(string what, Action fill, Action? pay)
        {
            Try(what, () =>
            {
                fill();
                pay?.Invoke();
                Click(main, "CheckoutButton");
                WaitText(main, "PosStatusText", t => t.Length > 0 && Rows(main, "CartGrid").Count == 0, "sale completed", TimeSpan.FromSeconds(20));
                if (Text(main, "PosErrorText") is { Length: > 0 } error) throw new InvalidOperationException(error);
                Step("     " + Text(main, "PosStatusText"));
            });
        }

        void Type(string sku, int quantity = 1)
        {
            SetText(main, "QuantityBox", quantity.ToString(CultureInfo.InvariantCulture));
            SetText(main, "ProductCodeBox", sku);
            Click(main, "AddButton");
            Until(() => Rows(main, "CartGrid").Any(r => r.Any(c => Products.Any(p => p.Sku == sku && c.Contains(p.Name, StringComparison.Ordinal)))) ? "ok" : null,
                "line " + sku, TimeSpan.FromSeconds(15));
        }

        Sell("sale 1: two scanned colas and water, cash 20 with change", () =>
        {
            Scan(main, "CartGrid", "5449000000996");
            Scan(main, "CartGrid", "5449000000996");
            Type("WATER-500", 2);
        }, () =>
        {
            Choose(main, "PaymentMethodBox", "Cash");
            SetText(main, "PaymentAmountBox", "20");
            Click(main, "AddPaymentButton");
            WaitText(main, "ChangeDueText", t => t.Length > 0, "change");
        });

        Sell("sale 2: coffee, milk and dates, paid by card", () =>
        {
            Type("COFFEE-250");
            Type("MILK-1L", 2);
            Type("DATES-500");
        }, () =>
        {
            var total = Text(main, "TotalText");
            Choose(main, "PaymentMethodBox", "Card");
            SetText(main, "PaymentAmountBox", total);
            Click(main, "AddPaymentButton");
            WaitText(main, "AmountDueText", t => t == Money(0m), "card covers the total");
        });

        Sell("sale 3: for the customer Sara Ahmed, chips and chocolate, cash", () =>
        {
            SetText(main, "CustomerSearchBox", "C001");
            Click(main, "FindCustomerButton");   // the only match is attached at once
            WaitText(main, "CustomerText", t => t.Contains("Sara", StringComparison.Ordinal), "customer on the sale");
            Type("CHIPS-150", 2);
            Type("CHOC-50", 3);
        }, null);

        // ---- the start file for the owner, then a clean close (the till and the shift stay open for testing)
        var desktop = Path.Combine(Repo.Root, "src", "Client", "Client.Desktop", "bin", Repo.Configuration, "net10.0-windows");
        var launcher = Path.Combine(Repo.Root, "src", "Client", "Client.Launcher", "bin", Repo.Configuration, "net10.0", "GenericPOS.exe");
        var cmd = new StringBuilder();
        cmd.AppendLine("@echo off");
        cmd.AppendLine("rem Opens the Matjar demo shop (built " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "). Your normal data is not touched.");
        foreach (var (key, value) in settings)
            cmd.AppendLine($"set \"GENERICPOS_{key.Replace(":", "__")}={value}\"");
        cmd.AppendLine(File.Exists(launcher)
            ? $"start \"\" \"{launcher}\" --app \"{desktop}\""
            : $"start \"\" \"{Path.Combine(desktop, "Client.Desktop.exe")}\"");
        File.WriteAllText(Path.Combine(Root, "Start Matjar demo.cmd"), cmd.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Root, "README.txt"),
            $"""
            Matjar demo shop - built {DateTime.Now:yyyy-MM-dd HH:mm}

            Open it:    double-click "Start Matjar demo.cmd" in this folder.
            Sign in:    user name  owner
                        password   {Password}

            In it: a 30-day demo license for every module, VAT 14% (prices include it), {Products.Length} products with barcodes
            (one with an Arabic name), the warehouses Main store and Back room with opening stock, 3 customers, 2 suppliers,
            the cash drawer shift open with a float of 100, the till open, and 3 sales (cash with change, card, a customer's sale).

            Barcodes to type or scan: {string.Join(", ", Products.Take(4).Select(p => p.Barcode + " (" + p.Name + ")"))} ...
            Receipts are "printed" into printed-receipts.bin (no printer needed); a keyboard-mode barcode scanner works as is.
            Your normal Matjar data is never used: everything is in this folder. Delete the folder to remove the demo.
            """, new UTF8Encoding(false));

        app.Close(main);
        Step($"Demo shop ready in {Root}; exit code {app.ExitCode}.");
    }
}
