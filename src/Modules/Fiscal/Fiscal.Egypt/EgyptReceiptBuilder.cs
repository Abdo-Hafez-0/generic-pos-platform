using System.Globalization;
using System.Text.Json;
using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Platform.Core.Results;

namespace Fiscal.Egypt;

/// <summary>MISS-11c: Egypt's settings (section "Fiscal:Egypt").</summary>
public sealed class EgyptReceiptOptions
{
    public const string SectionName = "Fiscal:Egypt";

    /// <summary>MISS-11c-3: the ETA's addresses in its pre-production (test) environment (ETA SDK, 2026-10-21).</summary>
    public EtaHosts PreProduction { get; set; } = new()
    {
        Identity = "https://id.preprod.eta.gov.eg", Api = "https://api.preprod.invoicing.eta.gov.eg", Portal = "https://preprod.invoicing.eta.gov.eg",
    };

    /// <summary>MISS-11c-3: the ETA's addresses in production.</summary>
    public EtaHosts Production { get; set; } = new()
    {
        Identity = "https://id.eta.gov.eg", Api = "https://api.invoicing.eta.gov.eg", Portal = "https://invoicing.eta.gov.eg",
    };

    /// <summary>The addresses of an environment (none chosen yet: production - the receipt's QR links to the real portal).</summary>
    public EtaHosts Hosts(FiscalEnvironment environment) => environment == FiscalEnvironment.PreProduction ? PreProduction : Production;

    /// <summary>A person buyer's name and national ID are required at or above this total (150,000 EGP for receipts v1.2).</summary>
    public decimal BuyerIdThreshold { get; set; } = 150_000m;

    public string Currency { get; set; } = "EGP";
}

/// <summary>MISS-11c-3: one ETA environment's addresses: the identity service (sign-in), the API and the portal (the QR's link).</summary>
public sealed class EtaHosts
{
    public string Identity { get; set; } = string.Empty;
    public string Api { get; set; } = string.Empty;
    public string Portal { get; set; } = string.Empty;
}

/// <summary>
/// MISS-11c: builds the ETA e-receipt (v1.2; owner's decision: the general Receipt, "s" for a sale and "r" for a return) of a sale or a return
/// at its place in the till's chain: the JSON, the UUID (SHA-256 of the serialized receipt with an empty uuid, which includes the previous
/// receipt's UUID) and the QR code's text. Pure and offline.
///
/// Matjar's prices include tax; the ETA's lines are net of tax: the net unit price and the net line are worked back with 5 decimals, the
/// line's tax is what the customer paid minus the net, so every line's total - and the receipt's total - is exactly what was charged
/// *(to confirm: the rounding the ETA allows between a line's tax and its net times the rate)*.
/// </summary>
internal static class EgyptReceiptBuilder
{
    public const string TypeVersion = "1.2";
    public const string SaleType = "s";
    public const string ReturnType = "r";
    public const int NationalIdLength = 14;
    public const int MaxLines = 300;

    /// <summary>The built receipt: the JSON sent, its UUID, the QR text, and the receipt as a tree (for the tests).</summary>
    public sealed record Built(string Json, string Uuid, string Qr, EtaObject Document);

    public static Result<Built> Build(FiscalBuildInput input, EgyptReceiptOptions options)
    {
        var request = input.Request;
        var settings = input.Settings;
        var isReturn = request.Kind == FiscalDocumentKindContract.Return;

        var problem = CheckSeller(settings, input.DeviceSerial);
        if (problem is not null) return Fail("Fiscal.Egypt.SellerIncomplete", problem);
        if (request.Lines.Count == 0) return Fail("Fiscal.Egypt.NoLines", "A receipt needs at least one line.");
        if (request.Lines.Count > MaxLines) return Fail("Fiscal.Egypt.TooManyLines", $"An e-receipt carries at most {MaxLines} lines.");
        if (isReturn && input.Original is null)
            return Fail("Fiscal.Egypt.NoOriginal",
                $"The sale {request.OriginalReference} has no electronic receipt to refer to, so its return cannot be sent to the tax authority.");

        // the lines, each with the authority's codes
        var missing = new List<string>();
        var lines = new List<EtaNode>();
        var sums = new LineSums();
        foreach (var line in request.Lines)
        {
            var codes = input.LineCodes?.GetValueOrDefault(line.ProductId);
            var lacks = new List<string>();
            if (codes?.ItemType is null || string.IsNullOrWhiteSpace(codes.ItemCode)) lacks.Add("item code");
            if (string.IsNullOrWhiteSpace(codes?.UnitCode)) lacks.Add("unit code");
            if (string.IsNullOrWhiteSpace(codes?.EgyptTaxType) || string.IsNullOrWhiteSpace(codes.EgyptSubType)) lacks.Add("tax type");
            if (lacks.Count > 0)
            {
                missing.Add($"'{line.Name}' ({string.Join(", ", lacks)})");
                continue;
            }

            if (line.Quantity <= 0m) return Fail("Fiscal.Egypt.QuantityInvalid", $"'{line.Name}' has no quantity.");
            lines.Add(Line(line, codes!, sums));
        }

        if (missing.Count > 0)
            return Fail("Fiscal.Egypt.CodesMissing", "Without the tax authority's codes: " + string.Join("; ", missing.Take(5))
                + (missing.Count > 5 ? $" and {missing.Count - 5} more" : string.Empty) + ". Set them on the Electronic receipts screen.");

        // the buyer: a person; named with the national ID at or above the threshold (a return: the sale's buyer)
        var (buyerName, buyerId) = isReturn ? OriginalBuyer(input.Original!.Payload) : (request.BuyerName, request.BuyerNationalId);
        buyerId = buyerId?.Trim();
        var rule = new FiscalBuyerRule(options.BuyerIdThreshold, NationalIdLength);
        if (rule.Applies(sums.Total) && !rule.IsSatisfiedBy(buyerName, buyerId))
            return Fail("Fiscal.Egypt.BuyerRequired",
                $"A receipt of {Money(options.BuyerIdThreshold)} or more must name the buyer: a name and a {NationalIdLength}-digit national ID.");
        var named = rule.IsSatisfiedBy(buyerName, buyerId);

        var issued = DateTime.SpecifyKind(request.IssuedAtUtc, DateTimeKind.Utc);
        var issuedText = issued.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        var header = EtaObject.Of(
            ("dateTimeIssued", new EtaText(issuedText)),
            ("receiptNumber", new EtaText(EtaJson.Clean(request.Reference, 50))),
            ("uuid", new EtaText(string.Empty)),
            ("previousUUID", new EtaText(input.PreviousHash ?? string.Empty)),   // empty only for the POS's first receipt
            ("referenceUUID", isReturn ? new EtaText(input.Original!.Hash) : null),
            ("referenceOldUUID", input.ReferenceOldHash is { Length: > 0 } old ? new EtaText(old) : null),   // MISS-11c-4: the corrected copy names the refused one
            ("currency", new EtaText(options.Currency)));

        var a = settings.Address;
        var seller = EtaObject.Of(
            ("rin", new EtaText(settings.TaxNumber!)),
            ("companyTradeName", new EtaText(EtaJson.Clean(settings.SellerNameArabic, 200))),
            ("branchCode", new EtaText(settings.BranchCode!)),
            ("branchAddress", EtaObject.Of(
                ("country", new EtaText("EG")),
                ("governate", new EtaText(EtaJson.Clean(a.Governorate, 100))),
                ("regionCity", new EtaText(EtaJson.Clean(a.City, 100))),
                ("street", new EtaText(EtaJson.Clean(a.Street, 200))),
                ("buildingNumber", new EtaText(EtaJson.Clean(a.BuildingNumber, 100))),
                ("postalCode", Optional(a.PostalCode, 30)),
                ("floor", Optional(a.Floor, 100)),
                ("room", Optional(a.Room, 100)),
                ("landmark", Optional(a.Landmark, 500)),
                ("additionalInformation", Optional(a.AdditionalInformation, 500)))),
            ("deviceSerialNumber", new EtaText(EtaJson.Clean(input.DeviceSerial, 100))),
            ("activityCode", new EtaText(settings.ActivityCode!)));

        var buyer = EtaObject.Of(
            ("type", new EtaText("P")),
            ("id", named ? new EtaText(buyerId!) : null),
            ("name", named ? new EtaText(EtaJson.Clean(buyerName, 100)) : null));

        var document = EtaObject.Of(
            ("header", header),
            ("documentType", EtaObject.Of(("receiptType", new EtaText(isReturn ? ReturnType : SaleType)), ("typeVersion", new EtaText(TypeVersion)))),
            ("seller", seller),
            ("buyer", buyer),
            ("itemData", new EtaArray(lines)),
            ("totalSales", new EtaNumber(sums.TotalSales)),
            ("totalCommercialDiscount", sums.Discount > 0m ? new EtaNumber(sums.Discount) : null),
            ("netAmount", new EtaNumber(sums.Net)),
            ("totalAmount", new EtaNumber(sums.Total)),
            ("taxTotals", new EtaArray(sums.Taxes.Select(t => (EtaNode)EtaObject.Of(("taxType", new EtaText(t.Key)), ("amount", new EtaNumber(t.Value)))).ToList())),
            ("paymentMethod", new EtaText(PaymentCode(request.Payments))));

        // the UUID: the serialized receipt with an empty uuid (it holds the previous receipt's UUID - the chain)
        var uuid = EtaJson.Hash(document);
        var final = document.With(["header", "uuid"], new EtaText(uuid));
        var qr = $"{options.Hosts(input.Environment).Portal.TrimEnd('/')}/receipts/search/{uuid}/share/{issuedText}#Total:{EtaNumber.Format(sums.Total)},IssuerRIN:{settings.TaxNumber}";
        return Result.Success(new Built(EtaJson.Write(final), uuid, qr, final));
    }

    /// <summary>
    /// Owner's decision: one payment sends its own code, a split payment the code of its largest part (Cash "C", Card "V", Other "O"); a sale
    /// without a recorded payment is cash.
    /// </summary>
    public static string PaymentCode(IReadOnlyList<FiscalPaymentRequest> payments)
    {
        var largest = payments.Where(p => p.Amount > 0m).OrderByDescending(p => p.Amount).FirstOrDefault();
        return largest?.Method switch
        {
            null or "Cash" => "C",
            "Card" => "V",
            _ => "O",
        };
    }

    private static EtaObject Line(FiscalLineRequest line, FiscalLineCodes codes, LineSums sums)
    {
        var factor = 1m + line.TaxRate;   // Matjar's rates are fractions (0.14 = 14%); the ETA's are percentages
        var unitPrice = R5(line.UnitPrice / factor);
        var totalSale = R5(line.Quantity * unitPrice);
        var netSale = line.Discount == 0m ? totalSale : Math.Min(R5(line.Total / factor), totalSale);
        var tax = line.Total - netSale;
        var discount = totalSale - netSale;
        sums.Add(totalSale, discount, netSale, line.Total, codes.EgyptTaxType!, tax);

        return EtaObject.Of(
            ("internalCode", new EtaText(EtaJson.Clean(string.IsNullOrWhiteSpace(line.ProductCode) ? line.ProductId.ToString("N") : line.ProductCode, 50))),
            ("description", new EtaText(EtaJson.Clean(line.Name, 500))),
            ("itemType", new EtaText(codes.ItemType == FiscalItemCodeType.GS1 ? "GS1" : "EGS")),
            ("itemCode", new EtaText(EtaJson.Clean(codes.ItemCode, 100))),
            ("unitType", new EtaText(EtaJson.Clean(codes.UnitCode, 30))),
            ("quantity", new EtaNumber(line.Quantity)),
            ("unitPrice", new EtaNumber(unitPrice)),
            ("netSale", new EtaNumber(netSale)),
            ("totalSale", new EtaNumber(totalSale)),
            ("total", new EtaNumber(line.Total)),
            ("commercialDiscountData", discount > 0m
                ? new EtaArray([EtaObject.Of(("amount", new EtaNumber(discount)), ("description", new EtaText("Discount")))])
                : null),
            ("taxableItems", new EtaArray(
            [
                EtaObject.Of(("taxType", new EtaText(codes.EgyptTaxType!)), ("amount", new EtaNumber(tax)), ("subType", new EtaText(codes.EgyptSubType!)),
                    ("rate", new EtaNumber(line.TaxRate * 100m))),
            ])));
    }

    /// <summary>What the seller's details lack for Egypt's receipt, or null.</summary>
    private static string? CheckSeller(FiscalSettings settings, string? deviceSerial)
    {
        var lacks = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.TaxNumber)) lacks.Add("the tax registration number");
        if (string.IsNullOrWhiteSpace(settings.BranchCode)) lacks.Add("the branch code");
        if (string.IsNullOrWhiteSpace(settings.ActivityCode)) lacks.Add("the activity code");
        if (string.IsNullOrWhiteSpace(settings.SellerNameArabic)) lacks.Add("the shop's legal name");
        var a = settings.Address;
        if (a.Governorate is null || a.City is null || a.Street is null || a.BuildingNumber is null)
            lacks.Add("the branch address (governorate, city, street and building number)");
        if (string.IsNullOrWhiteSpace(deviceSerial)) lacks.Add("the till's serial number");
        return lacks.Count == 0 ? null : "The shop's details for the tax authority are incomplete: " + string.Join(", ", lacks) + ". Complete them on the Electronic receipts screen.";
    }

    /// <summary>The buyer the sale's receipt named (a return names the same buyer).</summary>
    private static (string? Name, string? Id) OriginalBuyer(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            if (!json.RootElement.TryGetProperty("buyer", out var buyer)) return (null, null);
            return (buyer.TryGetProperty("name", out var n) ? n.GetString() : null, buyer.TryGetProperty("id", out var i) ? i.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static EtaText? Optional(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : new EtaText(EtaJson.Clean(value, max));

    private static decimal R5(decimal value) => decimal.Round(value, 5, MidpointRounding.AwayFromZero);

    private static string Money(decimal value) => value.ToString("#,0.00", CultureInfo.InvariantCulture);

    private static Result<Built> Fail(string code, string description) => Result.Failure<Built>(Error.Validation(code, description));

    /// <summary>The receipt's totals, summed from its lines (the tax per tax type, in the order first met).</summary>
    private sealed class LineSums
    {
        public decimal TotalSales { get; private set; }
        public decimal Discount { get; private set; }
        public decimal Net { get; private set; }
        public decimal Total { get; private set; }
        public List<KeyValuePair<string, decimal>> Taxes { get; } = [];

        public void Add(decimal totalSale, decimal discount, decimal net, decimal total, string taxType, decimal tax)
        {
            (TotalSales, Discount, Net, Total) = (TotalSales + totalSale, Discount + discount, Net + net, Total + total);
            var at = Taxes.FindIndex(t => t.Key == taxType);
            if (at < 0) Taxes.Add(new(taxType, tax));
            else Taxes[at] = new(taxType, Taxes[at].Value + tax);
        }
    }
}
