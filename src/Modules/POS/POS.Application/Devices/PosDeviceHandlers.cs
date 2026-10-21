using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using Platform.Application.Abstractions.Auditing;
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
/// <summary>
/// FIX-04: which cash drawer this till's cash sales go into ("PosCash" configuration section). One till = one drawer: a second till PC
/// sets its own code. The drawer shift itself is opened and counted on the Cash drawer screen (CashManagement).
/// </summary>
public sealed class PosCashOptions
{
    public const string SectionName = "PosCash";

    /// <summary>The drawer code of the shift that receives this till's cash sales (the Cash drawer screen's default is MAIN).</summary>
    public string DrawerCode { get; set; } = "MAIN";
}

/// <summary>MISS-10 (owner): how long after a sale it can be returned without the late-returns permission (setting "PosReturns:DaysAllowed").</summary>
public sealed class PosReturnOptions
{
    public const string SectionName = "PosReturns";

    /// <summary>Days after the sale during which anyone who can sell may accept a return; after it, only the late-returns permission.</summary>
    public int DaysAllowed { get; set; } = 14;
}

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
    public static ReceiptDocument Create(PosCart cart, PosSession session, Guid saleId, ReceiptPayment? payment, PosReceiptOptions options, DateTimeOffset fallbackTime,
        TimeZoneInfo? shopTimeZone = null)
        => CreateWithPayments(cart, session, saleId, payment is null ? [] : [payment], options, fallbackTime, shopTimeZone);

    /// <summary>FIX-10: one receipt payment per part (a single payment is printed as before).</summary>
    /// <param name="shopTimeZone">MISS-06b: the receipt shows the time on the shop's clock (the computer's time zone, from TimeProvider) - not
    /// UTC, which is how the sale is stored. Null keeps the stored instant as it is.</param>
    public static ReceiptDocument CreateWithPayments(PosCart cart, PosSession session, Guid saleId, IReadOnlyList<ReceiptPayment> payments, PosReceiptOptions options, DateTimeOffset fallbackTime,
        TimeZoneInfo? shopTimeZone = null)
    {
        var issuedAt = cart.CheckedOutAt is { } at
            ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc))
            : fallbackTime;
        if (shopTimeZone is not null) issuedAt = TimeZoneInfo.ConvertTime(issuedAt, shopTimeZone);

        var priced = cart.PricedLines;
        var lines = priced
            .Select(l => new ReceiptLine(l.Item.ProductName, l.Item.Quantity.Value, l.Item.UnitPrice.Amount, l.Amounts.Total, l.Amounts.Discount))
            .ToList();

        return new ReceiptDocument(
            options.StoreName,
            options.HeaderLines ?? [],
            "S-" + saleId.ToString("N")[..8].ToUpperInvariant(),
            issuedAt,
            session.CashierReference,
            lines,
            cart.Total.Amount,
            payments.Count == 1 ? payments[0] : null,
            options.FooterLines ?? [],
            // FIX-08b: the tax contained in the total, one line per rate (rates without tax are not printed)
            priced.Where(l => l.Amounts.Tax > 0m)
                .GroupBy(l => l.Item.TaxRate)
                .OrderByDescending(g => g.Key)
                .Select(g => new ReceiptTax(g.Key, g.Sum(l => l.Amounts.Tax)))
                .ToList(),
            payments.Count > 1 ? payments : null);
    }

    /// <summary>MISS-11c-2: the electronic receipt's mark as the receipt prints it (null: none).</summary>
    public static ReceiptFiscal? ToReceiptFiscal(Fiscal.Contracts.Models.FiscalReceiptMark? mark)
        => mark is null ? null : new ReceiptFiscal(mark.QrPayload, mark.Title, mark.Caption);

    public static ReceiptPayment ToReceiptPayment(POSPaymentRequest payment, decimal total, decimal changeDue)
        => new(payment.Method.ToString(), total, payment.TenderedAmount, payment.Method == POSPaymentMethod.Cash ? changeDue : null);

    /// <summary>A part of a (split) payment as the receipt prints it: its amount, and for cash what was tendered and the change.</summary>
    public static ReceiptPayment ToReceiptPayment(POSPaymentRequest part)
    {
        var amount = part.Amount ?? 0m;
        var change = part.Method == POSPaymentMethod.Cash && part.TenderedAmount is { } tendered ? Math.Max(0m, tendered - amount) : (decimal?)null;
        var method = string.IsNullOrWhiteSpace(part.MethodDetail) || part.Method == POSPaymentMethod.Cash ? part.Method.ToString() : $"{part.Method} ({part.MethodDetail!.Trim()})";
        return new(method, amount, part.TenderedAmount, part.Method == POSPaymentMethod.Cash ? change ?? 0m : null);
    }
}

// ---- explicit device operations (each returns a Result; hardware problems are failed results, never exceptions) ---------

public sealed record PrintReceiptCommand(Guid CartId, ReceiptPayment? Payment = null);

/// <summary>
/// Prints a completed sale's receipt AGAIN (MISS-07c: after a printer problem, or when asked): marked "COPY", with the payments as recorded
/// (read back from Payments - cash tendered and change, split parts), and audited whether or not the printer took it.
/// </summary>
public sealed class PrintReceiptCommandHandler(
    IPosCartRepository cartRepository,
    IPosSessionRepository sessionRepository,
    PosReceiptOptions options,
    TimeProvider timeProvider,
    IAuthorizationService authorization,
    IReceiptPrinter? printer = null,
    IPaymentReader? payments = null,
    IBusinessEventSink? events = null,
    Fiscal.Contracts.Interfaces.IFiscalReceiptMarks? fiscalMarks = null)   // MISS-11c-2: the copy carries the electronic receipt's QR
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

        var parts = await RecordedPaymentsAsync(saleId, cancellationToken);
        var receipt = (parts.Count > 0
                ? PosReceiptFactory.CreateWithPayments(cart, session, saleId, parts, options, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone)
                : PosReceiptFactory.Create(cart, session, saleId, command.Payment, options, timeProvider.GetUtcNow(), timeProvider.LocalTimeZone))
            with { IsCopy = true };
        if (fiscalMarks is not null)
            receipt = receipt with { Fiscal = PosReceiptFactory.ToReceiptFiscal(await fiscalMarks.GetMarkAsync(Fiscal.Contracts.Models.FiscalDocumentKindContract.Sale, saleId, cancellationToken)) };
        var printed = await HardwareGuard.RunAsync("receipt printer", () => printer.PrintAsync(receipt, cancellationToken));

        await events.TryRecordAsync(BusinessEvent.Create(POS.Application.Security.POSCapabilities.Module, "receipt.reprinted", "sale", saleId.ToString(),
            printed.IsSuccess
                ? $"A copy of receipt {receipt.Reference} ({cart.Total.Amount:0.00}) was printed."
                : $"A copy of receipt {receipt.Reference} could not be printed: {printed.Error.Description}"));
        return printed;
    }

    /// <summary>The sale's payments as recorded (voided ones left out), in the receipt's form; empty without the Payments module.</summary>
    private async Task<IReadOnlyList<ReceiptPayment>> RecordedPaymentsAsync(Guid saleId, CancellationToken cancellationToken)
    {
        if (payments is null) return [];
        var recorded = await payments.GetPaymentsForReferenceAsync("sale", saleId, cancellationToken);
        return recorded.Where(p => p.Status == PaymentStatusContract.Recorded)
            .OrderBy(p => p.RecordedAt)
            .Select(p => PosReceiptFactory.ToReceiptPayment(new POSPaymentRequest((POSPaymentMethod)(int)p.Method, p.TenderedAmount, p.MethodDetail, p.Amount)))
            .ToList();
    }
}

/// <summary>MISS-07c: the till's completed sales since a moment, for "Print the receipt again" (needs pos.receipt.reprint).</summary>
public sealed record GetRecentReceiptsQuery(DateTime SinceUtc);

public sealed class GetRecentReceiptsQueryHandler(IPosCartRepository cartRepository, IPosSessionRepository sessionRepository, IAuthorizationService authorization)
{
    public const int MaxReceipts = 200;

    public async Task<Result<IReadOnlyList<POSRecentReceipt>>> HandleAsync(GetRecentReceiptsQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.ReprintReceipt, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<IReadOnlyList<POSRecentReceipt>>(allowed.Error);

        var carts = await cartRepository.GetCheckedOutSinceAsync(query.SinceUtc, MaxReceipts, cancellationToken);
        var cashiers = new Dictionary<PosSessionId, string>();
        foreach (var sessionId in carts.Select(c => c.SessionId).Distinct())
            cashiers[sessionId] = (await sessionRepository.GetByIdAsync(sessionId, cancellationToken))?.CashierReference ?? string.Empty;

        return carts.Select(c => new POSRecentReceipt(c.Id.Value, c.SaleId!.Value, c.CheckedOutAt!.Value,
                "S-" + c.SaleId.Value.ToString("N")[..8].ToUpperInvariant(), c.Total.Amount, c.Items.Count, cashiers[c.SessionId]))
            .ToList();
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
