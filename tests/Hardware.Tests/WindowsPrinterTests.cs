using System.Diagnostics;
using Client.Hardware;
using Client.Hardware.Configuration;
using Client.Hardware.Transport;
using Platform.Application.Abstractions.Hardware;

namespace Hardware.Tests;

/// <summary>Runs only when GENERICPOS_TEST_PRINTER names a real printer installed in Windows: it PRINTS a test receipt and opens the drawer.</summary>
public sealed class RealPrinterFactAttribute : FactAttribute
{
    public RealPrinterFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GENERICPOS_TEST_PRINTER")))
            Skip = "Prints on a real printer: set GENERICPOS_TEST_PRINTER to its Windows name to run it (MISS-07).";
    }
}

/// <summary>
/// MISS-07a: receipt and label printers installed in Windows (usually USB with the maker's driver), reached RAW through the print queue.
/// Without a device: the queue states mapped to plain words, the factory, and the real Windows calls for a printer that does not exist and
/// for the list of installed printers. With a device (opt-in): a real receipt and a drawer pulse.
/// </summary>
public sealed class WindowsPrinterTests
{
    [Theory]
    [InlineData(0x00000000u, 0u)]           // idle
    [InlineData(0x00000200u, 0u)]           // busy
    [InlineData(0x00000400u, 0u)]           // printing
    [InlineData(0x00004000u, 0u)]           // processing
    [InlineData(0x01000000u, 0u)]           // power save
    [InlineData(0x00010000u, 0u)]           // warming up
    [InlineData(0x00020000u, 0u)]           // toner low (does not stop a receipt)
    public void A_queue_that_is_idle_busy_or_warming_up_is_ready(uint status, uint attributes)
        => Assert.Equal(DeviceState.Ready, WindowsPrinters.Describe("TM-T20", status, attributes, 0).State);

    [Theory]
    [InlineData(0x00000080u, 0u, "is offline (switched off or not connected)")]
    [InlineData(0x00001000u, 0u, "is not available")]
    [InlineData(0x00000010u, 0u, "is out of paper")]
    [InlineData(0x00000040u, 0u, "has a paper problem")]
    [InlineData(0x00000008u, 0u, "has a paper jam")]
    [InlineData(0x00400000u, 0u, "has its cover open")]
    [InlineData(0x00100000u, 0u, "needs attention")]
    [InlineData(0x00000002u, 0u, "reports an error")]
    [InlineData(0x00000001u, 0u, "is paused in Windows")]
    [InlineData(0x00000004u, 0u, "is being removed from Windows")]
    [InlineData(0x00000000u, 0x00000400u, "is set to 'Use Printer Offline' in Windows")]
    public void A_queue_that_cannot_print_says_why_in_plain_words(uint status, uint attributes, string why)
    {
        var described = WindowsPrinters.Describe("TM-T20", status, attributes, 0);

        Assert.Equal(DeviceState.Unavailable, described.State);
        Assert.Equal($"The printer 'TM-T20' {why}.", described.Message);
    }

    [Fact]
    public void Several_problems_and_waiting_jobs_are_all_said()
    {
        var described = WindowsPrinters.Describe("TM-T20", 0x00000080u | 0x00000010u, 0u, 3);

        Assert.Equal("The printer 'TM-T20' is offline (switched off or not connected) and is out of paper. 3 job(s) are waiting in its queue.", described.Message);
    }

    [Fact]
    public async Task The_factory_builds_windows_printers_drawers_and_label_printers_by_printer_name()
    {
        var missingName = HardwareFactory.CreateReceiptPrinter(new ReceiptPrinterOptions { Type = "EscPosWindows" });
        Assert.Contains("PrinterName", (await missingName.GetStatusAsync()).Message);

        var printer = HardwareFactory.CreateReceiptPrinter(new ReceiptPrinterOptions { Type = "escposwindows", PrinterName = "No Such Printer 7f3a" });
        Assert.DoesNotContain("Unknown", (await printer.GetStatusAsync()).Message ?? string.Empty);

        var drawer = HardwareFactory.CreateCashDrawer(new CashDrawerOptions { Type = "ViaReceiptPrinter" },
            new ReceiptPrinterOptions { Type = "EscPosWindows", PrinterName = "No Such Printer 7f3a" });
        Assert.DoesNotContain("needs an ESC/POS receipt printer", (await drawer.GetStatusAsync()).Message ?? string.Empty);

        var label = HardwareFactory.CreateLabelPrinter(new LabelPrinterOptions { Type = "ZplWindows", PrinterName = "No Such Printer 7f3a" });
        Assert.DoesNotContain("Unknown", (await label.GetStatusAsync()).Message ?? string.Empty);
    }

    [Fact]
    public async Task A_printer_that_is_not_installed_is_refused_quickly_in_plain_words_and_nothing_throws()
    {
        if (!OperatingSystem.IsWindows()) return;
        var transport = new WindowsPrinterTransport("No Such Printer 7f3a", TimeSpan.FromSeconds(5));
        var watch = Stopwatch.StartNew();

        var status = await transport.ProbeAsync();
        var sent = await transport.SendAsync(new byte[] { 0x1B, 0x40, 0x41, 0x0A });

        Assert.Equal(DeviceState.Unavailable, status.State);
        Assert.Equal("The Windows printer 'No Such Printer 7f3a' was not found. Check the name under Settings > Printers & scanners.", status.Message);
        Assert.Equal(HardwareErrors.UnavailableCode, sent.Error.Code);
        Assert.Contains("was not found", sent.Error.Description);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_installed_printers_are_listed_and_each_one_has_a_state_in_plain_words()
    {
        if (!OperatingSystem.IsWindows()) return;

        var installed = WindowsPrinters.Installed();

        Assert.Equal(installed.Order(StringComparer.OrdinalIgnoreCase), installed);
        foreach (var name in installed)
        {
            var status = WindowsPrinters.Status(name);   // read only: nothing is printed
            Assert.True(status.State == DeviceState.Ready || status.Message!.Contains(name, StringComparison.Ordinal), $"{name}: {status.Message}");
        }
    }

    [RealPrinterFact]
    public async Task A_real_printer_prints_a_test_receipt_and_opens_the_drawer()
    {
        var name = Environment.GetEnvironmentVariable("GENERICPOS_TEST_PRINTER")!;
        var options = new ReceiptPrinterOptions { Type = "EscPosWindows", PrinterName = name };
        var printer = HardwareFactory.CreateReceiptPrinter(options);
        Assert.Equal(DeviceState.Ready, (await printer.GetStatusAsync()).State);

        var receipt = new ReceiptDocument("GenericPOS device check", ["MISS-07 test receipt"], "TEST-0001", DateTimeOffset.Now, "tester",
            [new ReceiptLine("Test item", 2, 2.5m, 5m)], 5m, new ReceiptPayment("Cash", 5m, 10m, 5m), ["If you can read this, printing works."]);
        var printed = await printer.PrintAsync(receipt);
        Assert.True(printed.IsSuccess, printed.IsFailure ? printed.Error.Description : null);

        var drawer = HardwareFactory.CreateCashDrawer(new CashDrawerOptions { Type = "ViaReceiptPrinter" }, options);
        var opened = await drawer.OpenAsync();
        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Description : null);
    }
}
