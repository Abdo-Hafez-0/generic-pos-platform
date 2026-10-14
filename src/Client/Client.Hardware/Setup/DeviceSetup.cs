using System.Text.Json;
using Client.Hardware.Configuration;
using Client.Hardware.Printing;
using Client.Hardware.Transport;
using Client.Host.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;

namespace Client.Hardware.Setup;

/// <summary>
/// MISS-07b: the adapters' side of <see cref="IDeviceSetup"/>. It turns the shop's choices into the adapters' settings, makes devices for
/// one test through <see cref="HardwareFactory"/>, and keeps the choices in the installation's local settings file under the "Hardware"
/// section (read into the configuration at the next start). It carries no security: the POS module authorizes and audits every call.
/// </summary>
public sealed class DeviceSetupAdapter(
    HardwareOptions running,
    LocalSettingsFile? localSettings,
    Func<IReadOnlyList<string>> windowsPrinters,
    ILogger? logger = null,
    IReceiptImageRenderer? imageRenderer = null) : IDeviceSetup
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    private HardwareOptions SavedOptions => localSettings?.ReadSection<HardwareOptions>(HardwareOptions.SectionName) ?? running;

    public DeviceSetupSettings Saved => ToSetup(SavedOptions);

    public bool RestartNeeded => Fingerprint(SavedOptions) != Fingerprint(running);

    public IReadOnlyList<string> WindowsPrinters() => windowsPrinters();

    public async Task<DeviceSetupCheck> CheckAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default)
    {
        var options = ToOptions(settings);
        return new DeviceSetupCheck(
            await HardwareFactory.CreateReceiptPrinter(options.ReceiptPrinter, _logger, imageRenderer).GetStatusAsync(cancellationToken),
            await HardwareFactory.CreateCashDrawer(options.CashDrawer, options.ReceiptPrinter, _logger).GetStatusAsync(cancellationToken),
            await HardwareFactory.CreateLabelPrinter(options.LabelPrinter, _logger).GetStatusAsync(cancellationToken),
            await HardwareFactory.CreateScanner(options.Scanner, logger: _logger).GetStatusAsync(cancellationToken));
    }

    public Task<Result> PrintTestReceiptAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default)
    {
        var options = ToOptions(settings);
        var printer = HardwareFactory.CreateReceiptPrinter(options.ReceiptPrinter, _logger, imageRenderer);
        var receipt = new ReceiptDocument("GenericPOS", ["Test receipt"], "TEST", DateTimeOffset.Now, null,
            [new ReceiptLine("Test line", 1, 1.00m, 1.00m)], 1.00m, null, ["If you can read this, the printer works."]);
        return HardwareGuard.RunAsync("receipt printer", () => printer.PrintAsync(receipt, cancellationToken));
    }

    public Task<Result> OpenDrawerAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default)
    {
        var options = ToOptions(settings);
        var drawer = HardwareFactory.CreateCashDrawer(options.CashDrawer, options.ReceiptPrinter, _logger);
        return HardwareGuard.RunAsync("cash drawer", () => drawer.OpenAsync(cancellationToken));
    }

    public Task<Result> PrintTestLabelAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default)
    {
        var options = ToOptions(settings);
        var printer = HardwareFactory.CreateLabelPrinter(options.LabelPrinter, _logger);
        return HardwareGuard.RunAsync("label printer", () => printer.PrintAsync(new LabelDocument("Test label", "1234567890128", 1.00m), cancellationToken));
    }

    public Result Save(DeviceSetupSettings settings)
    {
        if (Validate(settings) is { } problem) return Result.Failure(Error.Validation("Devices.Invalid", problem));
        if (localSettings is null) return Result.Failure(Error.Failure("Devices.Unavailable", "Device settings cannot be kept on this installation."));
        try
        {
            localSettings.WriteSection(HardwareOptions.SectionName, ToOptions(settings));
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The device settings could not be saved.");
            return Result.Failure(Error.Failure("Devices.Failed", "The device settings could not be saved on this computer."));
        }
    }

    /// <summary>Why the settings cannot work, or null.</summary>
    public static string? Validate(DeviceSetupSettings settings)
    {
        foreach (var (name, printer) in new[] { ("receipt printer", settings.ReceiptPrinter), ("label printer", settings.LabelPrinter) })
        {
            if (printer.Link == DeviceLink.WindowsPrinter && string.IsNullOrWhiteSpace(printer.PrinterName))
                return $"Choose the Windows printer for the {name}.";
            if (printer.Link == DeviceLink.Network && (string.IsNullOrWhiteSpace(printer.Host) || printer.Port is < 1 or > 65535))
                return $"Enter the network address and port (1-65535) of the {name}.";
        }

        if (settings.DrawerThroughReceiptPrinter && settings.ReceiptPrinter.Link == DeviceLink.None)
            return "A cash drawer opened through the receipt printer needs a receipt printer.";
        return settings.CharactersPerLine is < 16 or > 80 ? "The characters per line must be between 16 and 80." : null;
    }

    // ------------------------------------------------------------------ the shop's words <-> the adapters' settings

    public static DeviceSetupSettings ToSetup(HardwareOptions options)
        => new(
            Printer(options.ReceiptPrinter.Type, "EscPos", options.ReceiptPrinter),
            options.ReceiptPrinter.CharactersPerLine,
            options.ReceiptPrinter.CutPaper,
            Is(options.CashDrawer.Type, "ViaReceiptPrinter"),
            options.CashDrawer.Pin,
            Is(options.Scanner.Type, "KeyboardWedge"),
            Printer(options.LabelPrinter.Type, "Zpl", options.LabelPrinter));

    /// <summary>The adapters' settings for the shop's choices; whatever the choices do not cover is taken from the saved settings.</summary>
    public HardwareOptions ToOptions(DeviceSetupSettings settings)
    {
        var options = JsonSerializer.Deserialize<HardwareOptions>(JsonSerializer.Serialize(SavedOptions))!;
        Apply(settings.ReceiptPrinter, "EscPos", options.ReceiptPrinter);
        options.ReceiptPrinter.CharactersPerLine = settings.CharactersPerLine;
        options.ReceiptPrinter.CutPaper = settings.CutPaper;

        if (settings.DrawerThroughReceiptPrinter)
        {
            options.CashDrawer.Type = "ViaReceiptPrinter";
            options.CashDrawer.Pin = settings.DrawerPin;
        }
        else if (Is(options.CashDrawer.Type, "ViaReceiptPrinter"))
        {
            options.CashDrawer.Type = "None";
        }

        options.Scanner.Type = settings.KeyboardScanner ? "KeyboardWedge" : "None";
        Apply(settings.LabelPrinter, "Zpl", options.LabelPrinter);
        return options;
    }

    private static PrinterSetup Printer(string? type, string protocol, DeviceConnectionOptions connection)
        => Is(type, protocol + "Windows") ? new PrinterSetup(DeviceLink.WindowsPrinter, connection.PrinterName, connection.Host, connection.Port)
            : Is(type, protocol + "Tcp") ? new PrinterSetup(DeviceLink.Network, connection.PrinterName, connection.Host, connection.Port)
            : string.IsNullOrWhiteSpace(type) || Is(type, "None") ? new PrinterSetup(DeviceLink.None, connection.PrinterName, connection.Host, connection.Port)
            : new PrinterSetup(DeviceLink.Configured, connection.PrinterName, connection.Host, connection.Port);

    private static void Apply(PrinterSetup choice, string protocol, DeviceConnectionOptions target)
    {
        switch (choice.Link)
        {
            case DeviceLink.None:
                Type(target, "None");
                break;
            case DeviceLink.WindowsPrinter:
                Type(target, protocol + "Windows");
                target.PrinterName = choice.PrinterName?.Trim();
                break;
            case DeviceLink.Network:
                Type(target, protocol + "Tcp");
                target.Host = choice.Host?.Trim();
                target.Port = choice.Port;
                break;
            // Configured: kept exactly as it is
        }
    }

    private static void Type(DeviceConnectionOptions target, string type)
    {
        switch (target)
        {
            case ReceiptPrinterOptions receipt: receipt.Type = type; break;
            case LabelPrinterOptions label: label.Type = type; break;
        }
    }

    private static bool Is(string? type, string expected) => string.Equals(type?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string Fingerprint(HardwareOptions options) => JsonSerializer.Serialize(options);
}
