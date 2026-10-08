using Platform.Core.Results;

namespace Platform.Application.Abstractions.Hardware;

// The five Stage 10 peripheral abstractions. They speak BUSINESS vocabulary (a barcode was scanned, print this receipt, open the
// drawer, what does it weigh) and know nothing about USB, serial, network, keyboard-wedge, ESC/POS, ZPL or any vendor SDK.
// Implementations live in infrastructure (Client.Hardware) and are chosen by configuration and dependency injection.

// ---- barcode scanner ---------------------------------------------------------------------------------------------

/// <summary>One scanned code. A scan is INPUT: it is never the source of truth for a sale.</summary>
public sealed record BarcodeScan(string Code, DateTimeOffset ScannedAt);

public sealed class BarcodeScannedEventArgs(BarcodeScan scan) : EventArgs
{
    public BarcodeScan Scan { get; } = scan;
}

public interface IBarcodeScanner : IHardwareDevice
{
    /// <summary>Raised for every scan while the scanner is started. A subscriber that throws never stops the scanner.</summary>
    event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    /// <summary>Starts listening. Fails (never throws) when there is no scanner or it cannot be opened; the POS keeps working with typed codes.</summary>
    Task<Result> StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

// ---- receipt printer ---------------------------------------------------------------------------------------------

/// <summary>One receipt line. Amounts are already calculated: printing never computes business values.</summary>
/// <param name="LineTotal">What is charged for the line, after its discount.</param>
/// <param name="Discount">The discount on the line (FIX-08c: its own and its share of a cart discount), 0 = none.</param>
public sealed record ReceiptLine(string Description, decimal Quantity, decimal UnitPrice, decimal LineTotal, decimal Discount = 0m);

/// <summary>The tax contained in the receipt total for one rate (prices include tax - FIX-08b), e.g. Rate 0.14, Amount 0.61.</summary>
public sealed record ReceiptTax(decimal Rate, decimal Amount);

/// <summary>How the sale was paid, when known.</summary>
public sealed record ReceiptPayment(string Method, decimal Amount, decimal? Tendered = null, decimal? Change = null);

/// <summary>Printer-independent receipt content.</summary>
public sealed record ReceiptDocument(
    string? StoreName,
    IReadOnlyList<string> HeaderLines,
    string Reference,
    DateTimeOffset IssuedAt,
    string? Cashier,
    IReadOnlyList<ReceiptLine> Lines,
    decimal Total,
    ReceiptPayment? Payment,
    IReadOnlyList<string> FooterLines,
    IReadOnlyList<ReceiptTax>? Taxes = null);

public interface IReceiptPrinter : IHardwareDevice
{
    Task<Result> PrintAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default);
}

// ---- label printer -----------------------------------------------------------------------------------------------

/// <summary>Printer-independent product/barcode label content.</summary>
public sealed record LabelDocument(string ProductName, string Code, decimal? Price, int Copies = 1);

public interface ILabelPrinter : IHardwareDevice
{
    Task<Result> PrintAsync(LabelDocument label, CancellationToken cancellationToken = default);
}

// ---- cash drawer -------------------------------------------------------------------------------------------------

public interface ICashDrawer : IHardwareDevice
{
    /// <summary>Opens the drawer. A failure is a hardware failure only: it never touches money records.</summary>
    Task<Result> OpenAsync(CancellationToken cancellationToken = default);
}

// ---- scale -------------------------------------------------------------------------------------------------------

public enum WeightUnit
{
    Gram = 1,
    Kilogram = 2,
    Ounce = 3,
    Pound = 4
}

/// <summary>A weight the application can use: a value, its unit, whether it has settled, and when it was read.</summary>
public sealed record WeightReading(decimal Value, WeightUnit Unit, bool IsStable, DateTimeOffset ReadAt)
{
    /// <summary>The weight in kilograms.</summary>
    public decimal Kilograms => Unit switch
    {
        WeightUnit.Gram => Value / 1000m,
        WeightUnit.Kilogram => Value,
        WeightUnit.Ounce => Value * 0.028349523125m,
        WeightUnit.Pound => Value * 0.45359237m,
        _ => throw new InvalidOperationException($"Unknown weight unit {Unit}.")
    };
}

public interface IScale : IHardwareDevice
{
    /// <summary>Reads the current weight. An unreadable or implausible reading is a failed result, never a made-up value.</summary>
    Task<Result<WeightReading>> ReadAsync(CancellationToken cancellationToken = default);
}
