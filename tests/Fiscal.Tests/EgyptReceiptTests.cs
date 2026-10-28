using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.Egypt;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11c: Egypt's e-receipt (ETA receipt v1.2) - the JSON, the serialized form and the UUID chain, the amounts net of tax, the codes,
/// the buyer above the threshold, returns referring to their sale, the payment code and the QR text. Checked against the rules published in
/// the ETA SDK (receipt v1.2, the document serialization approach, the receipt issuance FAQ).
/// </summary>
public sealed class EgyptReceiptTests
{
    private static readonly Guid Cola = Guid.NewGuid(), Chips = Guid.NewGuid();
    private static readonly DateTime IssuedAt = new(2026, 10, 21, 9, 30, 15, DateTimeKind.Utc);
    private static readonly EgyptReceiptOptions Options = new();

    private static FiscalSettings Settings(FiscalAddress? address = null)
    {
        var settings = FiscalSettings.Default();
        Assert.True(settings.Update(FiscalCountry.Egypt, "123456789", "0", "4711", "متجر \"النور\"",
            address ?? new FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12", Floor: "2"), false, "owner", IssuedAt).IsSuccess);
        return settings;
    }

    private static readonly Dictionary<Guid, FiscalLineCodes> Codes = new()
    {
        [Cola] = new(FiscalItemCodeType.GS1, "6221031490019", "EA", "T1", "V009", null, null, null),
        [Chips] = new(FiscalItemCodeType.EGS, "EG-123456789-CHIPS", "EA", "T1", "V009", null, null, null),
    };

    /// <summary>Cola 2 x 114.00 (14% included) and Chips 3 x 10.00 with 3.00 off: the till's tax-included amounts.</summary>
    private static FiscalDocumentRequest Sale(IReadOnlyList<FiscalPaymentRequest>? payments = null, string? buyerName = null, string? buyerId = null,
        decimal colaPrice = 114m)
    {
        var colaTotal = colaPrice * 2m;
        var colaTax = decimal.Round(colaTotal * 0.14m / 1.14m, 2);
        return new FiscalDocumentRequest(FiscalDocumentKindContract.Sale, Guid.NewGuid(), "S-1A2B3C4D", IssuedAt,
            [
                new FiscalLineRequest(Cola, "COLA", "كولا 1 لتر", 2m, colaPrice, 0m, colaTotal, 0.14m, colaTax),
                new FiscalLineRequest(Chips, "CHIPS", "Chips", 3m, 10m, 3m, 27m, 0.14m, 3.32m),
            ],
            colaTotal + 27m, colaTax + 3.32m, payments ?? [new FiscalPaymentRequest("Cash", colaTotal + 27m)], "owner", buyerName,
            BuyerNationalId: buyerId);
    }

    private static FiscalBuildInput Input(FiscalDocumentRequest request, string? previous = null, FiscalOriginalDocument? original = null,
        FiscalSettings? settings = null, Dictionary<Guid, FiscalLineCodes>? codes = null, string? serial = "MTJ-1A2B-3C4D-5E6F")
        => new(request, settings ?? Settings(), Guid.NewGuid(), "SHOP-PC", 1, previous, codes ?? Codes, original, serial);

    private static EgyptReceiptBuilder.Built Build(FiscalBuildInput input)
    {
        var built = EgyptReceiptBuilder.Build(input, Options);
        Assert.True(built.IsSuccess, built.IsFailure ? built.Error.Description : null);
        return built.Value;
    }

    /// <summary>The ETA's serialization written again from the parsed JSON (independently of Matjar's writer): names upper case and quoted,
    /// simple values quoted as written, an array's name before it and again before each element.</summary>
    private static string SerializeIndependently(JsonElement element)
    {
        var sb = new StringBuilder();
        void Properties(JsonElement obj)
        {
            foreach (var property in obj.EnumerateObject())
            {
                var name = "\"" + property.Name.ToUpperInvariant() + "\"";
                sb.Append(name);
                if (property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in property.Value.EnumerateArray()) { sb.Append(name); Value(item); }
                else Value(property.Value);
            }
        }

        void Value(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object: Properties(value); break;
                case JsonValueKind.String: sb.Append('"').Append(value.GetString()).Append('"'); break;
                default: sb.Append('"').Append(value.GetRawText()).Append('"'); break;
            }
        }

        Properties(element);
        return sb.ToString();
    }

    [Fact]
    public void The_serialized_form_follows_the_SDK_rules()
    {
        var document = EtaObject.Of(
            ("header", EtaObject.Of(("receiptNumber", new EtaText("S-1")), ("uuid", new EtaText("")))),
            ("itemData", new EtaArray([EtaObject.Of(("quantity", new EtaNumber(2m))), EtaObject.Of(("quantity", new EtaNumber(0.5m)))])),
            ("totalAmount", new EtaNumber(1000m)));

        Assert.Equal("\"HEADER\"\"RECEIPTNUMBER\"\"S-1\"\"UUID\"\"\"\"ITEMDATA\"\"ITEMDATA\"\"QUANTITY\"\"2.00\"\"ITEMDATA\"\"QUANTITY\"\"0.50\"\"TOTALAMOUNT\"\"1000.00\"",
            EtaJson.Serialize(document));
        Assert.Equal("{\"header\":{\"receiptNumber\":\"S-1\",\"uuid\":\"\"},\"itemData\":[{\"quantity\":2.00},{\"quantity\":0.50}],\"totalAmount\":1000.00}",
            EtaJson.Write(document));
    }

    [Fact]
    public void The_UUID_is_the_SHA256_of_the_receipt_as_sent_with_an_empty_uuid()
    {
        var built = Build(Input(Sale(), previous: new string('a', 64)));

        using var json = JsonDocument.Parse(built.Json);
        var header = json.RootElement.GetProperty("header");
        Assert.Equal(built.Uuid, header.GetProperty("uuid").GetString());
        Assert.Matches("^[0-9a-f]{64}$", built.Uuid);

        // the text that is sent, serialized again with the uuid emptied, hashes to the uuid
        var serialized = SerializeIndependently(json.RootElement).Replace("\"UUID\"\"" + built.Uuid + "\"", "\"UUID\"\"\"");
        Assert.Equal(built.Uuid, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))));
    }

    [Fact]
    public void Each_receipt_names_the_previous_ones_UUID_and_the_first_names_none()
    {
        var request = Sale();
        var first = Build(Input(request, previous: null));
        var second = Build(Input(request, previous: first.Uuid));

        using var a = JsonDocument.Parse(first.Json);
        using var b = JsonDocument.Parse(second.Json);
        Assert.Equal("", a.RootElement.GetProperty("header").GetProperty("previousUUID").GetString());
        Assert.Equal(first.Uuid, b.RootElement.GetProperty("header").GetProperty("previousUUID").GetString());
        Assert.NotEqual(first.Uuid, second.Uuid);   // the chain is in the hash
        Assert.Equal(first.Uuid, Build(Input(request, previous: null)).Uuid);   // the same receipt, the same UUID
    }

    [Fact]
    public void The_receipt_carries_the_seller_the_type_and_the_buyer()
    {
        var built = Build(Input(Sale()));
        using var json = JsonDocument.Parse(built.Json);
        var root = json.RootElement;

        Assert.Equal(["header", "documentType", "seller", "buyer", "itemData", "totalSales", "totalCommercialDiscount", "netAmount", "totalAmount", "taxTotals", "paymentMethod"],
            root.EnumerateObject().Select(p => p.Name));
        var header = root.GetProperty("header");
        Assert.Equal(("2026-10-21T09:30:15Z", "S-1A2B3C4D", "EGP"),
            (header.GetProperty("dateTimeIssued").GetString(), header.GetProperty("receiptNumber").GetString(), header.GetProperty("currency").GetString()));
        Assert.False(header.TryGetProperty("referenceUUID", out _));
        Assert.Equal(("s", "1.2"), (root.GetProperty("documentType").GetProperty("receiptType").GetString(), root.GetProperty("documentType").GetProperty("typeVersion").GetString()));

        var seller = root.GetProperty("seller");
        Assert.Equal(("123456789", "0", "4711", "MTJ-1A2B-3C4D-5E6F"), (seller.GetProperty("rin").GetString(), seller.GetProperty("branchCode").GetString(),
            seller.GetProperty("activityCode").GetString(), seller.GetProperty("deviceSerialNumber").GetString()));
        Assert.Equal("متجر ”النور”", seller.GetProperty("companyTradeName").GetString());   // a double quote never needs escaping
        var address = seller.GetProperty("branchAddress");
        Assert.Equal(["country", "governate", "regionCity", "street", "buildingNumber", "floor"], address.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("EG", "القاهرة", "12"), (address.GetProperty("country").GetString(), address.GetProperty("governate").GetString(), address.GetProperty("buildingNumber").GetString()));

        Assert.Equal(["type"], root.GetProperty("buyer").EnumerateObject().Select(p => p.Name));   // a person, below the threshold: no ID
        Assert.Equal("P", root.GetProperty("buyer").GetProperty("type").GetString());
        Assert.Contains("\"description\":\"كولا 1 لتر\"", built.Json);   // Arabic as it is, not escaped
    }

    [Fact]
    public void The_lines_are_net_of_tax_and_every_total_is_what_was_charged()
    {
        var built = Build(Input(Sale()));
        using var json = JsonDocument.Parse(built.Json);
        var root = json.RootElement;
        var lines = root.GetProperty("itemData").EnumerateArray().ToList();

        string Raw(JsonElement e, string name) => e.GetProperty(name).GetRawText();

        // Cola: 2 x 114.00 with 14% included = 2 x 100.00 net + 28.00 tax
        Assert.Equal(("GS1", "6221031490019", "EA", "COLA"), (lines[0].GetProperty("itemType").GetString(), lines[0].GetProperty("itemCode").GetString(),
            lines[0].GetProperty("unitType").GetString(), lines[0].GetProperty("internalCode").GetString()));
        Assert.Equal(("2.00", "100.00", "200.00", "200.00", "228.00"),
            (Raw(lines[0], "quantity"), Raw(lines[0], "unitPrice"), Raw(lines[0], "totalSale"), Raw(lines[0], "netSale"), Raw(lines[0], "total")));
        Assert.False(lines[0].TryGetProperty("commercialDiscountData", out _));
        var tax = lines[0].GetProperty("taxableItems")[0];
        Assert.Equal(("T1", "V009", "28.00", "14.00"), (tax.GetProperty("taxType").GetString(), tax.GetProperty("subType").GetString(), Raw(tax, "amount"), Raw(tax, "rate")));

        // Chips: 3 x 10.00 less 3.00 = 27.00 charged; net worked back with 5 decimals, the tax is the rest
        Assert.Equal(("8.77193", "26.31579", "23.68421", "27.00"), (Raw(lines[1], "unitPrice"), Raw(lines[1], "totalSale"), Raw(lines[1], "netSale"), Raw(lines[1], "total")));
        Assert.Equal("2.63158", Raw(lines[1].GetProperty("commercialDiscountData")[0], "amount"));
        Assert.Equal("3.31579", Raw(lines[1].GetProperty("taxableItems")[0], "amount"));

        Assert.Equal(("226.31579", "2.63158", "223.68421", "255.00"),
            (Raw(root, "totalSales"), Raw(root, "totalCommercialDiscount"), Raw(root, "netAmount"), Raw(root, "totalAmount")));
        var taxTotal = Assert.Single(root.GetProperty("taxTotals").EnumerateArray());
        Assert.Equal(("T1", "31.31579"), (taxTotal.GetProperty("taxType").GetString(), Raw(taxTotal, "amount")));
    }

    [Fact]
    public void The_QR_links_to_the_receipt_on_the_portal_with_its_total_and_the_sellers_RIN()
    {
        var built = Build(Input(Sale()));
        Assert.Equal($"https://invoicing.eta.gov.eg/receipts/search/{built.Uuid}/share/2026-10-21T09:30:15Z#Total:255.00,IssuerRIN:123456789", built.Qr);
    }

    [Theory]
    [InlineData("Cash", "C")]
    [InlineData("Card", "V")]
    [InlineData("Other", "O")]
    public void One_payment_sends_its_own_code(string method, string code)
        => Assert.Equal(code, EgyptReceiptBuilder.PaymentCode([new FiscalPaymentRequest(method, 255m)]));

    [Fact]
    public void A_split_payment_sends_the_code_of_its_largest_part_and_no_payment_is_cash()
    {
        Assert.Equal("V", EgyptReceiptBuilder.PaymentCode([new FiscalPaymentRequest("Cash", 55m), new FiscalPaymentRequest("Card", 200m)]));
        Assert.Equal("C", EgyptReceiptBuilder.PaymentCode([new FiscalPaymentRequest("Cash", 200m), new FiscalPaymentRequest("Card", 55m)]));
        Assert.Equal("C", EgyptReceiptBuilder.PaymentCode([]));

        using var json = JsonDocument.Parse(Build(Input(Sale([new FiscalPaymentRequest("Cash", 55m), new FiscalPaymentRequest("Card", 200m)]))).Json);
        Assert.Equal("V", json.RootElement.GetProperty("paymentMethod").GetString());
    }

    [Fact]
    public void At_or_above_the_threshold_the_receipt_names_the_buyer_with_the_national_ID()
    {
        var big = Sale(colaPrice: 75_000m);   // 150,027.00
        var refused = EgyptReceiptBuilder.Build(Input(big), Options);
        Assert.Equal("Fiscal.Egypt.BuyerRequired", refused.Error.Code);
        Assert.Equal("Fiscal.Egypt.BuyerRequired", EgyptReceiptBuilder.Build(Input(Sale(buyerName: "أحمد", buyerId: "2990101", colaPrice: 75_000m)), Options).Error.Code);

        var named = Build(Input(Sale(buyerName: "أحمد علي", buyerId: "29901011234567", colaPrice: 75_000m)));
        using var json = JsonDocument.Parse(named.Json);
        var buyer = json.RootElement.GetProperty("buyer");
        Assert.Equal(("P", "29901011234567", "أحمد علي"), (buyer.GetProperty("type").GetString(), buyer.GetProperty("id").GetString(), buyer.GetProperty("name").GetString()));
        Assert.Equal(new FiscalBuyerRule(150_000m, 14), new EgyptCountryPart().BuyerRule);
    }

    [Fact]
    public void A_return_is_a_return_receipt_naming_its_sales_UUID_and_buyer()
    {
        var sale = Build(Input(Sale(buyerName: "أحمد علي", buyerId: "29901011234567", colaPrice: 75_000m)));
        var request = Sale() with
        {
            Kind = FiscalDocumentKindContract.Return, Reference = "R-0000AAAA", OriginalReference = "S-1A2B3C4D", OriginalSourceId = Guid.NewGuid(),
            Lines = [new FiscalLineRequest(Cola, "COLA", "كولا 1 لتر", 2m, 75_000m, 0m, 150_000m, 0.14m, 18_421.05m)], Total = 150_000m,
        };

        var returned = Build(Input(request, previous: sale.Uuid, original: new FiscalOriginalDocument(Guid.NewGuid(), sale.Uuid, sale.Json, IssuedAt)));
        using var json = JsonDocument.Parse(returned.Json);
        var root = json.RootElement;
        Assert.Equal("r", root.GetProperty("documentType").GetProperty("receiptType").GetString());
        Assert.Equal(sale.Uuid, root.GetProperty("header").GetProperty("referenceUUID").GetString());
        Assert.Equal(sale.Uuid, root.GetProperty("header").GetProperty("previousUUID").GetString());
        Assert.Equal("29901011234567", root.GetProperty("buyer").GetProperty("id").GetString());   // the sale's buyer, above the threshold

        // MISS-11e: without the sale's receipt and without the sale's time, nothing can be sent
        var orphan = EgyptReceiptBuilder.Build(Input(request), Options);
        Assert.Equal("Fiscal.Egypt.SaleTimeUnknown", orphan.Error.Code);
        Assert.Contains("S-1A2B3C4D", orphan.Error.Description);
    }

    /// <summary>MISS-11e: a return of a sale made before e-receipts were on - ETA's Return Receipt Without Reference.</summary>
    private static FiscalDocumentRequest ReturnOfOldSale(DateTime soldAt, string? buyerName = null, string? buyerId = null, decimal colaPrice = 114m)
        => Sale(buyerName: buyerName, buyerId: buyerId, colaPrice: colaPrice) with
        {
            Kind = FiscalDocumentKindContract.Return, Reference = "R-0000AAAA", OriginalReference = "S-0LD5A1E0", OriginalSourceId = Guid.NewGuid(),
            OriginalIssuedAtUtc = soldAt,
        };

    [Fact]
    public void A_return_of_a_sale_made_before_e_receipts_is_a_return_receipt_without_reference()
    {
        var soldAt = new DateTime(2026, 9, 30, 17, 5, 9, DateTimeKind.Utc);
        var built = Build(Input(ReturnOfOldSale(soldAt), previous: new string('b', 64)));

        using var json = JsonDocument.Parse(built.Json);
        var root = json.RootElement;
        var header = root.GetProperty("header");
        Assert.Equal("RWR", root.GetProperty("documentType").GetProperty("receiptType").GetString());
        Assert.Equal("1.2", root.GetProperty("documentType").GetProperty("typeVersion").GetString());
        Assert.False(header.TryGetProperty("referenceUUID", out _));
        Assert.Equal("B", header.GetProperty("documentUseReason").GetString());   // sales issued before adoption
        Assert.Equal("2026-09-30T17:05:09Z", header.GetProperty("salesIssuedDateTime").GetString());
        Assert.Equal("S-0LD5A1E0", header.GetProperty("sOrderNameCode").GetString());
        Assert.Equal(new string('b', 64), header.GetProperty("previousUUID").GetString());   // in the chain like any receipt
        Assert.Equal(["dateTimeIssued", "receiptNumber", "uuid", "previousUUID", "currency", "sOrderNameCode", "documentUseReason", "salesIssuedDateTime"],
            header.EnumerateObject().Select(p => p.Name).ToArray());   // the SDK's order
        Assert.Equal(built.Uuid, header.GetProperty("uuid").GetString());
    }

    [Fact]
    public void A_large_return_without_reference_names_the_buyer_asked_at_the_till()
    {
        var soldAt = IssuedAt.AddDays(-3);
        var refused = EgyptReceiptBuilder.Build(Input(ReturnOfOldSale(soldAt, colaPrice: 75_000m)), Options);
        Assert.Equal("Fiscal.Egypt.BuyerRequired", refused.Error.Code);

        var named = Build(Input(ReturnOfOldSale(soldAt, "أحمد علي", "29901011234567", colaPrice: 75_000m)));
        using var json = JsonDocument.Parse(named.Json);
        var buyer = json.RootElement.GetProperty("buyer");
        Assert.Equal("P", buyer.GetProperty("type").GetString());
        Assert.Equal("29901011234567", buyer.GetProperty("id").GetString());
        Assert.Equal("أحمد علي", buyer.GetProperty("name").GetString());
    }

    [Fact]
    public void A_return_of_a_sale_whose_receipt_is_not_made_yet_waits_for_it()
    {
        var input = Input(ReturnOfOldSale(IssuedAt.AddDays(-1))) with { OriginalPending = true };
        var refused = EgyptReceiptBuilder.Build(input, Options);

        Assert.Equal("Fiscal.Egypt.OriginalNotMade", refused.Error.Code);
        Assert.Contains("S-0LD5A1E0", refused.Error.Description);
    }

    [Fact]
    public void A_return_more_than_540_days_after_its_sale_gets_no_e_receipt()
    {
        Assert.True(EgyptReceiptBuilder.Build(Input(ReturnOfOldSale(IssuedAt.AddDays(-540))), Options).IsSuccess);
        var late = EgyptReceiptBuilder.Build(Input(ReturnOfOldSale(IssuedAt.AddDays(-541))), Options);
        Assert.Equal("Fiscal.Egypt.ReturnTooLate", late.Error.Code);
        Assert.Contains("540", late.Error.Description);

        // a return naming its sale's receipt too
        var sale = Build(Input(Sale()));
        var request = Sale() with { Kind = FiscalDocumentKindContract.Return, Reference = "R-0000BBBB", OriginalReference = "S-1A2B3C4D", OriginalSourceId = Guid.NewGuid() };
        var tooLate = EgyptReceiptBuilder.Build(Input(request, original: new FiscalOriginalDocument(Guid.NewGuid(), sale.Uuid, sale.Json, IssuedAt.AddDays(-600))), Options);
        Assert.Equal("Fiscal.Egypt.ReturnTooLate", tooLate.Error.Code);
    }

    [Fact]
    public void Egypts_part_takes_returns_for_540_days_and_names_the_buyer_of_one_without_reference()
    {
        IFiscalCountryPart part = new EgyptCountryPart();
        Assert.Equal(540, part.ReturnDaysAllowed);
        Assert.True(part.ReturnWithoutOriginalNamesBuyer);
    }

    [Fact]
    public void A_line_without_the_authoritys_codes_is_refused_naming_the_product_and_what_is_missing()
    {
        var codes = new Dictionary<Guid, FiscalLineCodes> { [Cola] = Codes[Cola], [Chips] = new(null, null, "EA", null, null, null, null, null) };
        var refused = EgyptReceiptBuilder.Build(Input(Sale(), codes: codes), Options);

        Assert.Equal("Fiscal.Egypt.CodesMissing", refused.Error.Code);
        Assert.Contains("'Chips' (item code, tax type)", refused.Error.Description);
        Assert.DoesNotContain("كولا", refused.Error.Description);
    }

    [Fact]
    public void Incomplete_seller_details_or_no_serial_are_refused_with_what_is_missing()
    {
        var noSerial = EgyptReceiptBuilder.Build(Input(Sale(), serial: null), Options);
        Assert.Equal("Fiscal.Egypt.SellerIncomplete", noSerial.Error.Code);
        Assert.Contains("serial number", noSerial.Error.Description);

        // settings saved before 11c had one address line (now the street)
        var old = FiscalSettings.Default();
        typeof(FiscalSettings).GetProperty(nameof(FiscalSettings.Country))!.SetValue(old, FiscalCountry.Egypt);
        foreach (var (name, value) in new[] { ("TaxNumber", "123456789"), ("BranchCode", "0"), ("ActivityCode", "4711"), ("SellerNameArabic", "متجر"), ("Street", "شارع 9") })
            typeof(FiscalSettings).GetProperty(name)!.SetValue(old, value);
        var noAddress = EgyptReceiptBuilder.Build(Input(Sale(), settings: old), Options);
        Assert.Equal("Fiscal.Egypt.SellerIncomplete", noAddress.Error.Code);
        Assert.Contains("branch address", noAddress.Error.Description);
    }

    [Fact]
    public void Egypts_mark_is_the_QR_with_the_Arabic_title_and_the_short_UUID()
    {
        // MISS-11c-2 (owner's choice, drawn option A)
        var uuid = "68e656b2" + new string('0', 48) + "f368f320";
        Assert.Equal(new FiscalReceiptMark("qr", "إيصال إلكتروني", "UUID 68e656b2...f368f320"), new EgyptCountryPart().Mark(uuid, "qr"));
    }

    [Fact]
    public async Task The_part_builds_and_waits_for_the_tills_registration_before_sending()
    {
        var part = new EgyptCountryPart();
        var built = part.Build(Input(Sale()));
        Assert.True(built.IsSuccess);
        Assert.StartsWith("https://invoicing.eta.gov.eg/receipts/search/" + built.Value.Hash, built.Value.QrPayload);

        var outgoing = new FiscalOutgoingDocument(Guid.NewGuid(), FiscalDocumentKind.Sale, "S-1", 1, built.Value.Hash, built.Value.Payload, IssuedAt, null);
        var sent = Assert.Single(await part.SendAsync(new FiscalSendInput([outgoing], Settings(), Guid.NewGuid(), "SHOP-PC", FiscalTillRegistration.NotRegistered)));
        Assert.Equal((FiscalSendResult.NotSent, EgyptCountryPart.NotRegistered), (sent.Result, sent.Reason));
    }
}
