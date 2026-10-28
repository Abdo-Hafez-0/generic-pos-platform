using System.Text.Json;
using Fiscal.Application.Commands;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Commands;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Sales.Contracts.Interfaces;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// MISS-11c on the real host and SQLite file with Egypt's own part (no stand-in): a sale and its return become ETA receipts in the same
/// transaction - the JSON with its UUID chained to the previous one, the QR, the return naming its sale's UUID; a sale at or above the
/// threshold is refused until the buyer is named (owner's decision: asked at the till). MISS-11e: a return of a sale made before e-receipts
/// were on becomes a Return Receipt Without Reference (its buyer asked at the till above the threshold); a sale whose receipt is not made yet
/// is made first.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class EgyptReceiptIntegrationTests
{
    private static async Task<T> InScopeAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>The real host with Egypt's receipts on and the product, its unit and the 14% VAT coded for the ETA.</summary>
    private static async Task<(IntegrationHost Host, Shop Shop)> StartAsync(decimal salePrice, bool receiptsOn = true)
    {
        var host = await IntegrationHost.StartAllAsync();
        if (receiptsOn) await SwitchOnAsync(host);

        var shop = await CreateShopAsync(host.Services, salePrice: salePrice, stock: 10m);
        using (var scope = host.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<Pricing.Application.Commands.CreateTaxRateCommandHandler>()
                .HandleAsync(new Pricing.Application.Commands.CreateTaxRateCommand("VAT14", "VAT 14%", 0.14m, true))).IsSuccess);
        if (receiptsOn) await CodeAsync(host, shop);
        return (host, shop);
    }

    /// <summary>Egypt's receipts on, with the shop's details.</summary>
    private static async Task SwitchOnAsync(IntegrationHost host)
    {
        var saved = await InScopeAsync(host.Services, sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.Egypt, "123456789", "0", "4711", "متجر",
                new FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12"))));
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Description : null);
    }

    /// <summary>The product, its unit and the 14% VAT coded for the ETA.</summary>
    private static async Task CodeAsync(IntegrationHost host, Shop shop)
    {
        var codes = await InScopeAsync(host.Services, async sp => (await sp.GetRequiredService<Fiscal.Application.Queries.GetFiscalCodesQueryHandler>()
            .HandleAsync(new Fiscal.Application.Queries.GetFiscalCodesQuery())).Value);
        var coded = await InScopeAsync(host.Services, sp => sp.GetRequiredService<SaveFiscalCodesCommandHandler>().HandleAsync(new SaveFiscalCodesCommand(
            Products: [new(shop.ProductId, FiscalItemCodeType.EGS, "EG-123456789-" + shop.Sku)], Units: [new(codes.Products[0].UnitId, "EA")],
            TaxRates: [new(codes.TaxRates[0].TaxRateId, "T1", "V009", null, null, null)])));
        Assert.True(coded.IsSuccess, coded.IsFailure ? coded.Error.Description : null);
    }

    private static async Task<List<(long Kind, string? Hash, string? PreviousHash, string? Payload, string? Qr, long State, string? Reason)>> DocumentsAsync(IntegrationHost host)
    {
        await using var connection = await host.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Kind, Hash, PreviousHash, Payload, QrPayload, State, StateReason FROM fis_Documents ORDER BY CreatedAt, Number";
        var rows = new List<(long, string?, string?, string?, string?, long, string?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        return rows;
    }

    [Fact]
    public async Task A_sale_and_its_return_become_ETA_receipts_chained_by_their_UUIDs()
    {
        var (host, shop) = await StartAsync(salePrice: 11.40m);
        await using var _h = host;
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 3m);
        var sale = await CheckoutAsync(host.Services, cartId, tendered: 50m);
        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        Assert.DoesNotContain(sale.HardwareNotices ?? [], n => n.Device == "electronic receipt");

        var line = (await InScopeAsync(host.Services, sp => sp.GetRequiredService<ISalesReader>().GetReturnableAsync(sale.SaleId)))!.Lines[0];
        var refund = await InScopeAsync(host.Services, sp => sp.GetRequiredService<RefundSaleCommandHandler>()
            .HandleAsync(new RefundSaleCommand(sale.SaleId, [new RefundLine(line.SaleItemId, 1m)], "Changed mind", POSPaymentMethod.Cash)));
        Assert.True(refund.IsSuccess, refund.IsFailure ? refund.Error.Description : null);

        var documents = await DocumentsAsync(host);
        Assert.Equal(2, documents.Count);
        Assert.All(documents, d => Assert.Equal((long)FiscalDocumentState.Waiting, d.State));
        var (saleDoc, returnDoc) = (documents[0], documents[1]);

        using var saleJson = JsonDocument.Parse(saleDoc.Payload!);
        var header = saleJson.RootElement.GetProperty("header");
        Assert.Equal((saleDoc.Hash, "", "s"), (header.GetProperty("uuid").GetString(), header.GetProperty("previousUUID").GetString(),
            saleJson.RootElement.GetProperty("documentType").GetProperty("receiptType").GetString()));
        var item = saleJson.RootElement.GetProperty("itemData")[0];
        Assert.Equal(("EGS", "EG-123456789-" + shop.Sku, "EA", "34.20", "10.00"), (item.GetProperty("itemType").GetString(), item.GetProperty("itemCode").GetString(),
            item.GetProperty("unitType").GetString(), item.GetProperty("total").GetRawText(), item.GetProperty("unitPrice").GetRawText()));
        Assert.Matches("^MTJ-", saleJson.RootElement.GetProperty("seller").GetProperty("deviceSerialNumber").GetString());
        Assert.Equal($"https://invoicing.eta.gov.eg/receipts/search/{saleDoc.Hash}/share/{header.GetProperty("dateTimeIssued").GetString()}#Total:34.20,IssuerRIN:123456789", saleDoc.Qr);

        using var returnJson = JsonDocument.Parse(returnDoc.Payload!);
        var returnHeader = returnJson.RootElement.GetProperty("header");
        Assert.Equal("r", returnJson.RootElement.GetProperty("documentType").GetProperty("receiptType").GetString());
        Assert.Equal((saleDoc.Hash, saleDoc.Hash, returnDoc.Hash), (returnHeader.GetProperty("referenceUUID").GetString(),
            returnHeader.GetProperty("previousUUID").GetString(), returnHeader.GetProperty("uuid").GetString()));
        Assert.Equal("11.40", returnJson.RootElement.GetProperty("totalAmount").GetRawText());

        // MISS-11c-2: the paper's mark - the QR, "إيصال إلكتروني" and the short UUID - for a copy of the sale and on the return receipt
        var mark = await InScopeAsync(host.Services, sp => sp.GetRequiredService<Fiscal.Contracts.Interfaces.IFiscalReceiptMarks>()
            .GetMarkAsync(Fiscal.Contracts.Models.FiscalDocumentKindContract.Sale, sale.SaleId));
        Assert.Equal(new Fiscal.Contracts.Models.FiscalReceiptMark(saleDoc.Qr!, "إيصال إلكتروني", "UUID " + saleDoc.Hash![..8] + "..." + saleDoc.Hash[^8..]), mark);
        Assert.Equal(returnDoc.Qr, refund.Value.FiscalMark?.QrPayload);
        var paper = await InScopeAsync(host.Services, sp => Task.FromResult(sp.GetRequiredService<RefundSaleCommandHandler>()
            .ReturnReceipt(new RefundSaleCommand(sale.SaleId, [new RefundLine(line.SaleItemId, 1m)], "Changed mind", POSPaymentMethod.Cash), refund.Value,
                new POS.Application.Devices.PosReceiptOptions())));
        Assert.Equal(returnDoc.Qr, paper.Fiscal?.QrPayload);
        Assert.Equal("إيصال إلكتروني", paper.Fiscal?.Title);
    }

    [Fact]
    public async Task A_return_of_a_sale_made_before_e_receipts_is_a_return_receipt_without_reference_naming_the_buyer_asked_at_the_till()
    {
        var (host, shop) = await StartAsync(salePrice: 75_000m, receiptsOn: false);
        await using var _h = host;
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 2m);
        var sale = await CheckoutAsync(host.Services, cartId, tendered: 150_000m);
        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        await SwitchOnAsync(host);
        await CodeAsync(host, shop);
        Assert.Empty(await DocumentsAsync(host));   // sold before e-receipts were on

        // the till learns that the return names its buyer above the threshold, and the ETA's 540 days
        var returnable = (await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSReader>().GetReturnableSaleAsync(sale.SaleId)))!;
        Assert.Equal(new POSFiscalReturnRule(new POSFiscalBuyerRule(150_000m, 14), 540), returnable.Fiscal);
        var line = returnable.Lines[0];

        var refused = await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSService>().RefundSaleAsync(sale.SaleId,
            [new POSRefundLineRequest(line.SaleItemId, 2m)], "Changed mind", POSPaymentMethod.Card));
        Assert.Equal("POS.Refund.BuyerRequired", refused.ErrorCode);
        Assert.Equal(0, await CountAsync(host, "sal_Returns", "1 = 1"));   // nothing was saved
        Assert.Empty(await DocumentsAsync(host));

        var refunded = await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSService>().RefundSaleAsync(sale.SaleId,
            [new POSRefundLineRequest(line.SaleItemId, 2m)], "Changed mind", POSPaymentMethod.Card, buyer: new POSBuyer("أحمد علي", "29901011234567")));
        Assert.True(refunded.IsSuccess, refunded.ErrorMessage);
        Assert.DoesNotContain(refunded.HardwareNotices ?? [], n => n.Device == "electronic receipt");

        var document = Assert.Single(await DocumentsAsync(host));
        Assert.Equal((long)FiscalDocumentState.Waiting, document.State);
        using var json = JsonDocument.Parse(document.Payload!);
        var root = json.RootElement;
        var header = root.GetProperty("header");
        Assert.Equal("RWR", root.GetProperty("documentType").GetProperty("receiptType").GetString());
        Assert.False(header.TryGetProperty("referenceUUID", out _));
        Assert.Equal(("B", returnable.CompletedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), returnable.ReceiptNumber),
            (header.GetProperty("documentUseReason").GetString(), header.GetProperty("salesIssuedDateTime").GetString(), header.GetProperty("sOrderNameCode").GetString()));
        Assert.Equal(("29901011234567", "أحمد علي"), (root.GetProperty("buyer").GetProperty("id").GetString(), root.GetProperty("buyer").GetProperty("name").GetString()));
        Assert.Equal("150000.00", root.GetProperty("totalAmount").GetRawText());
    }

    [Fact]
    public async Task A_return_of_a_sale_whose_receipt_is_not_made_yet_waits_for_the_sales_receipt()
    {
        var (host, shop) = await StartAsync(salePrice: 11.40m);
        await using var _h = host;
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);
        var sale = await CheckoutAsync(host.Services, cartId, tendered: 20m);
        Assert.True(sale.IsSuccess, sale.ErrorMessage);

        // a sale with its receipt: the return takes its buyer from it, the ETA's days still apply
        var returnable = (await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSReader>().GetReturnableSaleAsync(sale.SaleId)))!;
        Assert.Equal(new POSFiscalReturnRule(null, 540), returnable.Fiscal);

        // the sale's receipt as if it could not be made
        await using (var connection = await host.OpenDatabaseAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"UPDATE fis_Documents SET State = {(int)FiscalDocumentState.NotBuilt}, Hash = NULL, Payload = NULL, Number = NULL";
            await command.ExecuteNonQueryAsync();
        }

        var refunded = await InScopeAsync(host.Services, sp => sp.GetRequiredService<RefundSaleCommandHandler>()
            .HandleAsync(new RefundSaleCommand(sale.SaleId, [new RefundLine(returnable.Lines[0].SaleItemId, 1m)], "Changed mind", POSPaymentMethod.Cash)));
        Assert.True(refunded.IsSuccess, refunded.IsFailure ? refunded.Error.Description : null);   // the refund stands
        Assert.Contains("Make the sale's receipt first", refunded.Value.FiscalNotBuilt);
        var documents = await DocumentsAsync(host);
        Assert.Equal((long)FiscalDocumentState.NotBuilt, documents[1].State);
    }

    [Fact]
    public async Task A_sale_at_the_threshold_is_refused_until_the_buyer_is_named()
    {
        var (host, shop) = await StartAsync(salePrice: 75_000m);
        await using var _h = host;
        var rule = await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSReader>().GetFiscalBuyerRuleAsync());
        Assert.Equal(new POSFiscalBuyerRule(150_000m, 14), rule);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 2m);

        var refused = await CheckoutAsync(host.Services, cartId, tendered: 150_000m);
        Assert.False(refused.IsSuccess);
        Assert.Equal("POS.Checkout.BuyerRequired", refused.ErrorCode);
        Assert.Equal(0, await CountAsync(host, "sal_Sales", "Status = 3"));
        Assert.Empty(await DocumentsAsync(host));

        var halfNamed = await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSService>().CheckoutAsync(cartId,
            payment: new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 150_000m), buyer: new POSBuyer("أحمد علي", "2990101")));
        Assert.Equal("POS.Checkout.BuyerRequired", halfNamed.ErrorCode);

        var sold = await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSService>().CheckoutAsync(cartId,
            payment: new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 150_000m), buyer: new POSBuyer(" أحمد علي ", "29901011234567")));
        Assert.True(sold.IsSuccess, sold.ErrorMessage);
        var document = Assert.Single(await DocumentsAsync(host));
        Assert.Equal((long)FiscalDocumentState.Waiting, document.State);
        using var json = JsonDocument.Parse(document.Payload!);
        var buyer = json.RootElement.GetProperty("buyer");
        Assert.Equal(("P", "29901011234567", "أحمد علي"), (buyer.GetProperty("type").GetString(), buyer.GetProperty("id").GetString(), buyer.GetProperty("name").GetString()));
    }
}
