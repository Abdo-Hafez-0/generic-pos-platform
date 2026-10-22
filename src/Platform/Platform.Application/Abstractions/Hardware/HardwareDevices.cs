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
/// <param name="WrittenOff">MISS-10c, on a return receipt: the units were damaged (written off, not back on the shelf).</param>
public sealed record ReceiptLine(string Description, decimal Quantity, decimal UnitPrice, decimal LineTotal, decimal Discount = 0m, bool WrittenOff = false);

/// <summary>MISS-10c: what makes a receipt a RETURN receipt - the receipt of the sale it returns and the reason given.</summary>
public sealed record ReceiptReturn(string OriginalReference, string Reason);

/// <summary>The tax contained in the receipt total for one rate (prices include tax - FIX-08b), e.g. Rate 0.14, Amount 0.61.</summary>
public sealed record ReceiptTax(decimal Rate, decimal Amount);

/// <summary>
/// MISS-11c-2: the tax authority's mark on a receipt - the QR code of its electronic receipt (printed offline, owner's choice: large and
/// centred under the totals) with a title and a caption under it (Egypt: "إيصال إلكتروني" and the short UUID).
/// </summary>
public sealed record ReceiptFiscal(string QrPayload, string Title, string Caption)
{
    /// <summary>MISS-11d-2: lines under the shop's name (Saudi Arabia: the invoice's title and the VAT number).</summary>
    public IReadOnlyList<string>? TopLines { get; init; }

    /// <summary>MISS-11d-2: the authority's document's totals, printed instead of the total and its tax lines (null: the till's).</summary>
    public ReceiptVatTotals? VatTotals { get; init; }

    /// <summary>MISS-11d-2: every label in Arabic and English (Saudi Arabia: the invoice must be in Arabic).</summary>
    public bool BilingualLabels { get; init; }
}

/// <summary>MISS-11d-2: an invoice's totals as the authority reads them - excluding VAT, the VAT, including VAT.</summary>
public sealed record ReceiptVatTotals(decimal TotalExcludingVat, decimal Vat, decimal TotalIncludingVat);

/// <summary>How the sale was paid, when known.</summary>
public sealed record ReceiptPayment(string Method, decimal Amount, decimal? Tendered = null, decimal? Change = null);

/// <summary>Printer-independent receipt content. FIX-10: a split payment is in <see cref="Payments"/> (one entry per part).</summary>
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
    IReadOnlyList<ReceiptTax>? Taxes = null,
    IReadOnlyList<ReceiptPayment>? Payments = null,
    bool IsCopy = false,   // MISS-07c: a reprint says so on the paper
    ReceiptReturn? Return = null,   // MISS-10c: a return receipt (Lines are what came back, Total the refund, Payments how it was given)
    ReceiptFiscal? Fiscal = null)   // MISS-11c-2: the electronic receipt's QR code
{
    /// <summary>Every payment to print (FIX-10: a split payment has several; a single one may come as <see cref="Payment"/>).</summary>
    public IReadOnlyList<ReceiptPayment> AllPayments => Payments is { Count: > 0 } several ? several : Payment is { } one ? [one] : [];
}

/// <summary>
/// One line of a receipt laid out for printing (FIX-13a): <see cref="Left"/> starts the line, <see cref="Right"/> (an amount) ends it,
/// <see cref="Center"/> centres <see cref="Left"/> alone; <see cref="Rule"/> is a separator line. Texts may be in any script.
/// </summary>
/// MISS-11c-2: a line with <see cref="QrPayload"/> is the QR code of that text, centred, <see cref="QrDots"/> dots wide with its quiet zone.
public sealed record ReceiptImageLine(string Left, string Right = "", bool Bold = false, bool Center = false, bool Rule = false, string? QrPayload = null)
{
    /// <summary>The QR code's size on paper: 240 dots, 30 mm on a 203 dpi (8 dots/mm) receipt printer.</summary>
    public const int QrDots = 240;

    /// <summary>
    /// Dots per module for a code of <paramref name="size"/> modules with its 4-module quiet zone on each side: the whole number nearest to
    /// <see cref="QrDots"/> wide (owner's choice: about 30 mm), never wider than the paper, at least 1 - so every module is whole dots.
    /// </summary>
    public static int QrModuleDots(int size, int availableDots)
    {
        var dot = (int)Math.Round((double)QrDots / (size + 8), MidpointRounding.AwayFromZero);
        while (dot > 1 && (size + 8) * dot > availableDots) dot--;
        return Math.Max(1, dot);
    }
}

/// <summary>A black-and-white picture: <see cref="Rows"/> holds <see cref="Height"/> rows of <see cref="Width"/>/8 bytes, most significant bit = leftmost dot, 1 = black.</summary>
public sealed record MonochromeImage(int Width, int Height, byte[] Rows);

/// <summary>
/// Draws receipt lines as a picture with a real font (FIX-13a), so text a receipt printer's character set cannot print - Arabic with joined
/// letters and right-to-left order, accents, other scripts - is printed as an image. Implemented by the desktop (it has the fonts).
/// </summary>
public interface IReceiptImageRenderer
{
    /// <summary>Renders the lines <paramref name="widthDots"/> dots wide (a multiple of 8; 576 for 80 mm, 384 for 58 mm paper).</summary>
    MonochromeImage Render(IReadOnlyList<ReceiptImageLine> lines, int widthDots);
}

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
