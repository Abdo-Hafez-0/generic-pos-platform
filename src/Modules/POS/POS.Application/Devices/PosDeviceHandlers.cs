using Platform.Application.Abstractions.Authorization;
using Catalog.Contracts.Interfaces;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;
using POS.Application.Repositories;
using POS.Contracts.Models;
using POS.Domain.Entities;
using POS.Domain.Enums;
using POS.Domain.ValueObjects;

namespace POS.Application.Devices;

/// <summary>
/// Business-side receipt settings (store name, header/footer text, whether checkout prints and kicks the drawer by itself).
/// Business configuration: it says WHAT goes on a receipt, never HOW or on which printer.
/// </summary>
public sealed class PosReceiptOptions
{
    public const string SectionName = "PosReceipt";

    public string? StoreName { get; set; }
    public string[] HeaderLines { get; set; } = [];
    public string[] FooterLines { get; set; } = [];

    /// <summary>Print the receipt automatically after a completed checkout (when a receipt printer is configured).</summary>
    public bool AutoPrintReceipt { get; set; } = true;

    /// <summary>Open the cash drawer automatically after a completed CASH checkout (when a drawer is configured).</summary>
    public bool AutoOpenDrawerOnCashSale { get; set; } = true;
}

/// <summary>Builds the printer-independent receipt of a checked-out cart. Pure: no I/O, no hardware.</summary>
public static class PosReceiptFactory
{
    public static ReceiptDocument Create(PosCart cart, PosSession session, Guid saleId, ReceiptPayment? payment, PosReceiptOptions options, DateTimeOffset fallbackTime)
    {
        var issuedAt = cart.CheckedOutAt is { } at
            ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc))
            : fallbackTime;

        var lines = cart.Items
            .Select(i => new ReceiptLine(i.ProductName, i.Quantity.Value, i.UnitPrice.Amount, i.LineTotal.Amount))
            .ToList();

        return new ReceiptDocument(
            options.StoreName,
            options.HeaderLines ?? [],
            "S-" + saleId.ToString("N")[..8].ToUpperInvariant(),
            issuedAt,
            session.CashierReference,
            lines,
            cart.Total.Amount,
            payment,
            options.FooterLines ?? []);
    }

    public static ReceiptPayment ToReceiptPayment(POSPaymentRequest payment, decimal total, decimal changeDue)
        => new(payment.Method.ToString(), total, payment.TenderedAmount, payment.Method == POSPaymentMethod.Cash ? changeDue : null);
}

// ---- explicit device operations (each returns a Result; hardware problems are failed results, never exceptions) ---------

public sealed record PrintReceiptCommand(Guid CartId, ReceiptPayment? Payment = null);

public sealed class PrintReceiptCommandHandler(
    IPosCartRepository cartRepository,
    IPosSessionRepository sessionRepository,
    PosReceiptOptions options,
    TimeProvider timeProvider,
    IAuthorizationService authorization,
    IReceiptPrinter? printer = null)
{
    public async Task<Result> HandleAsync(PrintReceiptCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.ReprintReceipt, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var cart = await cartRepository.GetByIdAsync(new PosCartId(command.CartId), cancellationToken);
        if (cart is null)
            return Error.NotFound("POS.Receipt.CartNotFound", $"Cart '{command.CartId}' was not found.");

        if (cart.Status != PosCartStatus.CheckedOut || cart.SaleId is not { } saleId)
            return Error.Conflict("POS.Receipt.NotCheckedOut", "A receipt can only be printed for a cart that has been checked out.");

        var session = await sessionRepository.GetByIdAsync(cart.SessionId, cancellationToken);
        if (session is null)
            return Error.NotFound("POS.Receipt.SessionNotFound", "The cart's POS session was not found.");

        if (printer is null)
            return HardwareErrors.NotConfigured("receipt printer");

        var receipt = PosReceiptFactory.Create(cart, session, saleId, command.Payment, options, timeProvider.GetUtcNow());
        return await HardwareGuard.RunAsync("receipt printer", () => printer.PrintAsync(receipt, cancellationToken));
    }
}

public sealed class OpenCashDrawerCommandHandler(IAuthorizationService authorization, ICashDrawer? drawer = null)
{
    /// <summary>"No sale" drawer opens are a classic way to take cash out unnoticed, so they need their own capability.</summary>
    public async Task<Result> HandleAsync(CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.OpenDrawer, cancellationToken);
        if (allowed.IsFailure) return allowed;

        return drawer is null
            ? HardwareErrors.NotConfigured("cash drawer")
            : await HardwareGuard.RunAsync("cash drawer", () => drawer.OpenAsync(cancellationToken));
    }
}

public sealed record PrintProductLabelCommand(string ProductCode, int Copies = 1);

public sealed class PrintProductLabelCommandHandler(
    IProductBarcodeResolver barcodeResolver,
    IProductLookup productLookup,
    IAuthorizationService authorization,
    ILabelPrinter? printer = null)
{
    public async Task<Result> HandleAsync(PrintProductLabelCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.PrintLabel, cancellationToken);
        if (allowed.IsFailure) return allowed;

        if (string.IsNullOrWhiteSpace(command.ProductCode))
            return Error.Validation("POS.Label.CodeRequired", "A product barcode or SKU is required.");

        if (printer is null)
            return HardwareErrors.NotConfigured("label printer");

        var code = command.ProductCode.Trim();
        var product = await barcodeResolver.ResolveAsync(code, cancellationToken)
                      ?? await productLookup.FindBySkuAsync(code, cancellationToken);
        if (product is null)
            return Error.NotFound("POS.Label.ProductNotFound", $"No product with barcode or SKU '{code}' was found.");

        // The SKU is what is encoded: the POS resolves a SKU exactly like a barcode when it is scanned back.
        var label = new LabelDocument(product.Name, product.Sku, product.SalePrice, command.Copies);
        return await HardwareGuard.RunAsync("label printer", () => printer.PrintAsync(label, cancellationToken));
    }
}

public sealed class ReadWeightQueryHandler(IAuthorizationService authorization, IScale? scale = null)
{
    /// <summary>The heaviest weight (kg) treated as plausible; anything above is reported as unusable instead of being used.</summary>
    public const decimal MaxPlausibleKilograms = 1000m;

    public async Task<Result<WeightReading>> HandleAsync(CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.CreateSale, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<WeightReading>(allowed.Error);

        if (scale is null)
            return Result.Failure<WeightReading>(HardwareErrors.NotConfigured("scale"));

        var read = await HardwareGuard.RunAsync("scale", () => scale.ReadAsync(cancellationToken));
        if (read.IsFailure)
            return read;

        var reading = read.Value;
        if (reading is null || !Enum.IsDefined(reading.Unit) || reading.Value < 0m || reading.Kilograms > MaxPlausibleKilograms)
            return Result.Failure<WeightReading>(HardwareErrors.InvalidData("scale", "the reading is negative or implausible."));

        return read;
    }
}

public sealed class GetDeviceStatusQueryHandler(
    IBarcodeScanner? scanner = null,
    IReceiptPrinter? receiptPrinter = null,
    ILabelPrinter? labelPrinter = null,
    ICashDrawer? cashDrawer = null,
    IScale? scale = null)
{
    public async Task<IReadOnlyList<POSDeviceStatusResult>> HandleAsync(CancellationToken cancellationToken = default)
    {
        var devices = new (string Name, IHardwareDevice? Device)[]
        {
            ("barcode scanner", scanner), ("receipt printer", receiptPrinter), ("label printer", labelPrinter),
            ("cash drawer", cashDrawer), ("scale", scale)
        };

        var results = new List<POSDeviceStatusResult>();
        foreach (var (name, device) in devices)
        {
            var status = device is null ? DeviceStatus.NotConfigured() : await HardwareGuard.StatusAsync(device, cancellationToken);
            results.Add(new POSDeviceStatusResult(name, status.State.ToString(), status.Message));
        }

        return results;
    }
}
