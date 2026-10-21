using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Hardware;
using POS.Contracts.Interfaces;
using Tests.Common.Hardware;

namespace POS.Tests.Application;

/// <summary>
/// MISS-11c-2: the printed receipt carries the electronic receipt's QR code, its title and caption - at checkout (from the document made in
/// the sale's transaction) and on a copy printed later (read back from the Fiscal module); without a document nothing is added.
/// </summary>
public sealed class PosFiscalReceiptTests
{
    private static readonly FiscalReceiptMark Mark = new("https://invoicing.eta.gov.eg/receipts/search/abc/share/2026-10-21T09:30:15Z#Total:30.00,IssuerRIN:123456789",
        "إيصال إلكتروني", "UUID 68e656b2...f368f320");

    /// <summary>The Fiscal module as POS sees it: the document of each sale (with its mark, or none) and the marks read back.</summary>
    private sealed class FakeFiscal : IFiscalRecorder, IFiscalReceiptMarks
    {
        public bool Issues { get; set; } = true;
        public Dictionary<Guid, FiscalReceiptMark> Marks { get; } = [];

        public Task<FiscalDocumentResult> RecordAsync(FiscalDocumentRequest request, CancellationToken cancellationToken = default)
        {
            if (!Issues) return Task.FromResult(FiscalDocumentResult.NothingToIssue());
            Marks[request.SourceId] = Mark;
            return Task.FromResult(FiscalDocumentResult.Issued(Guid.NewGuid(), Marks.Count, Mark.QrPayload) with { Mark = Mark });
        }

        public Task<FiscalReceiptMark?> GetMarkAsync(FiscalDocumentKindContract kind, Guid sourceId, CancellationToken cancellationToken = default)
            => Task.FromResult(kind == FiscalDocumentKindContract.Sale ? Marks.GetValueOrDefault(sourceId) : null);
    }

    private static async Task<(PosTestDatabase Db, FakeReceiptPrinter Printer, FakeFiscal Fiscal)> StartAsync()
    {
        var printer = new FakeReceiptPrinter();
        var fiscal = new FakeFiscal();
        var db = await PosTestDatabase.CreateAsync(configureServices: s =>
        {
            s.AddSingleton<IReceiptPrinter>(printer);
            s.AddSingleton<IFiscalRecorder>(fiscal);
            s.AddSingleton<IFiscalReceiptMarks>(fiscal);
        });
        return (db, printer, fiscal);
    }

    private static async Task<(Guid CartId, Guid SaleId)> SellAsync(PosTestDatabase db)
    {
        var product = db.Catalog.Register("SKU-1", "Item", 10m, null);
        db.Inventory.SetStock(product, 100m);
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await service.OpenSessionAsync("cashier-7", Guid.NewGuid());
        var cart = await service.StartCartAsync(session.SessionId);
        Assert.True((await service.AddProductAsync(cart.CartId, "SKU-1", 3m)).IsSuccess);
        var sale = await service.CheckoutAsync(cart.CartId, null, null);
        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        return (cart.CartId, sale.SaleId);
    }

    [Fact]
    public async Task The_receipt_and_its_copy_carry_the_electronic_receipts_QR()
    {
        var (db, printer, _) = await StartAsync();
        await using var _db = db;
        var (cartId, _) = await SellAsync(db);

        var printed = Assert.Single(printer.Printed);
        Assert.Equal(new ReceiptFiscal(Mark.QrPayload, Mark.Title, Mark.Caption), printed.Fiscal);

        using (var scope = db.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IPOSDevices>().PrintReceiptAsync(cartId)).IsSuccess);
        var copy = printer.Printed[1];
        Assert.True(copy.IsCopy);
        Assert.Equal(printed.Fiscal, copy.Fiscal);
    }

    [Fact]
    public async Task Without_an_electronic_receipt_the_paper_has_no_QR()
    {
        var (db, printer, fiscal) = await StartAsync();
        await using var _db = db;
        fiscal.Issues = false;
        var (cartId, _) = await SellAsync(db);

        Assert.Null(Assert.Single(printer.Printed).Fiscal);
        using (var scope = db.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IPOSDevices>().PrintReceiptAsync(cartId)).IsSuccess);
        Assert.Null(printer.Printed[1].Fiscal);
    }
}
