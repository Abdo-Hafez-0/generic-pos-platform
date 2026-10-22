using System.Text;
using System.Xml.Linq;
using Fiscal.Application.Commands;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Commands;
using POS.Contracts.Models;
using Sales.Contracts.Interfaces;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// MISS-11d-1 on the real host and SQLite file with Saudi Arabia's own part: a sale becomes a simplified tax invoice and its return a
/// simplified credit note with the return's reason (owner's decision: credit notes in 11d), in the same transaction - chained by the ICV
/// and the PIH, the QR's first five fields printed before the till is onboarded.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SaudiInvoiceIntegrationTests
{
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private static async Task<T> InScopeAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    [Fact]
    public async Task A_sale_and_its_return_become_a_simplified_invoice_and_credit_note_chained_by_their_hashes()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var saved = await InScopeAsync(host.Services, sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.SaudiArabia, "300000000000003", null, null, "متجر النور",
                new FiscalAddress(null, "الرياض", "طريق الملك فهد", "2322", "12214", District: "العليا"), "CRN", "1010010000")));
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Description : null);
        var shop = await CreateShopAsync(host.Services, salePrice: 11.50m, stock: 10m);
        using (var scope = host.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<Pricing.Application.Commands.CreateTaxRateCommandHandler>()
                .HandleAsync(new Pricing.Application.Commands.CreateTaxRateCommand("VAT15", "VAT 15%", 0.15m, true))).IsSuccess);
        var codes = await InScopeAsync(host.Services, async sp => (await sp.GetRequiredService<Fiscal.Application.Queries.GetFiscalCodesQueryHandler>()
            .HandleAsync(new Fiscal.Application.Queries.GetFiscalCodesQuery())).Value);
        Assert.Equal((0, 0, 1), (codes.MissingProducts, codes.MissingUnits, codes.MissingTaxRates));   // Saudi Arabia needs the VAT category only
        Assert.True((await InScopeAsync(host.Services, sp => sp.GetRequiredService<SaveFiscalCodesCommandHandler>().HandleAsync(new SaveFiscalCodesCommand(
            TaxRates: [new(codes.TaxRates[0].TaxRateId, null, null, "S", null, null)])))).IsSuccess);

        var (_, cartId) = await OpenCartAsync(host.Services, shop, 3m);
        var sale = await CheckoutAsync(host.Services, cartId, tendered: 50m);
        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        Assert.DoesNotContain(sale.HardwareNotices ?? [], n => n.Device == "electronic receipt");

        var line = (await InScopeAsync(host.Services, sp => sp.GetRequiredService<ISalesReader>().GetReturnableAsync(sale.SaleId)))!.Lines[0];
        var refund = await InScopeAsync(host.Services, sp => sp.GetRequiredService<RefundSaleCommandHandler>()
            .HandleAsync(new RefundSaleCommand(sale.SaleId, [new RefundLine(line.SaleItemId, 1m)], "Wrong size", POSPaymentMethod.Card)));
        Assert.True(refund.IsSuccess, refund.IsFailure ? refund.Error.Description : null);

        var documents = new List<(string Hash, string Payload, string Qr)>();
        await using (var connection = await host.OpenDatabaseAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Hash, Payload, QrPayload FROM fis_Documents WHERE State = 1 ORDER BY Number";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) documents.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        Assert.Equal(2, documents.Count);
        var (invoice, note) = (XDocument.Parse(documents[0].Payload).Root!, XDocument.Parse(documents[1].Payload).Root!);
        string Pih(XElement root) => root.Elements(Cac + "AdditionalDocumentReference").Single(r => (string?)r.Element(Cbc + "ID") == "PIH").Descendants(Cbc + "EmbeddedDocumentBinaryObject").Single().Value;

        Assert.Equal(("388", "S-" + sale.SaleId.ToString("N")[..8].ToUpperInvariant()), (invoice.Element(Cbc + "InvoiceTypeCode")!.Value, invoice.Element(Cbc + "ID")!.Value));
        Assert.Equal("34.50", invoice.Element(Cac + "LegalMonetaryTotal")!.Element(Cbc + "PayableAmount")!.Value);   // 3 x 11.50
        Assert.Equal("30.00", invoice.Element(Cac + "LegalMonetaryTotal")!.Element(Cbc + "TaxExclusiveAmount")!.Value);
        Assert.Equal(("381", invoice.Element(Cbc + "ID")!.Value, "Wrong size", "48"), (note.Element(Cbc + "InvoiceTypeCode")!.Value,
            note.Element(Cac + "BillingReference")!.Value, note.Element(Cac + "PaymentMeans")!.Element(Cbc + "InstructionNote")!.Value,
            note.Element(Cac + "PaymentMeans")!.Element(Cbc + "PaymentMeansCode")!.Value));
        Assert.Equal(documents[0].Hash, Pih(note));
        Assert.Equal("11.50", note.Element(Cac + "LegalMonetaryTotal")!.Element(Cbc + "PayableAmount")!.Value);

        // the QR printed before onboarding: tags 1-5, the time in Riyadh
        var qr = Convert.FromBase64String(documents[0].Qr);
        Assert.Equal((1, 2), (qr[0], qr[2 + qr[1]]));
        Assert.Contains("300000000000003", Encoding.UTF8.GetString(qr));
    }
}
