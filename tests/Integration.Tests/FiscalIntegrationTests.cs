using System.Security.Cryptography;
using System.Text;
using Client.Host.Hosting;
using Fiscal.Application.Abstractions;
using Fiscal.Application.Commands;
using Fiscal.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Core.Results;
using POS.Application.Commands;
using POS.Contracts.Models;
using Sales.Contracts.Interfaces;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// MISS-11a-2 on the real host and SQLite file: with electronic receipts on (Egypt) and a country part installed, every sale and every return
/// gets its fiscal document IN THE SAME TRANSACTION - chained per till, the return naming its sale; a document that cannot be saved means
/// no sale; a document the part cannot build leaves the sale standing and says so (owner's decision: keep selling).
/// </summary>
[Collection(HostCollection.Name)]
public sealed class FiscalIntegrationTests
{
    /// <summary>A country part standing in for Egypt's (11c): the hash is the SHA-256 of a payload naming the document and the previous hash.</summary>
    private sealed class StandInPart : IFiscalCountryPart
    {
        public string? Refuse { get; set; }
        public FiscalCountry Country => FiscalCountry.Egypt;

        public Result<FiscalBuiltDocument> Build(FiscalBuildInput input)
        {
            if (Refuse is { } reason) return Result.Failure<FiscalBuiltDocument>(Error.Validation("Test.Refused", reason));
            var payload = $"{input.Request.Kind}|{input.Request.Reference}|{input.Number}|{input.PreviousHash}|{input.Request.Total:0.00}|{input.Request.TaxTotal:0.00}|{input.Request.OriginalReference}";
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            return Result.Success(new FiscalBuiltDocument(payload, hash, "QR:" + hash));
        }

        // the authority is out of reach in these tests: nothing is handed over (11a-3's sender is tested in Fiscal.Tests)
        public Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(input.Documents.Select(d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.NotSent, Reason: "offline")).ToList());

        public Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>([]);
    }

    private sealed class PartModule(StandInPart part) : IHostingModule
    {
        public void RegisterServices(HostBuilderContext context, IServiceCollection services) => services.AddSingleton<IFiscalCountryPart>(part);
    }

    private static async Task<T> InScopeAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private static async Task<(IntegrationHost Host, StandInPart Part)> StartWithEgyptAsync()
    {
        var part = new StandInPart();
        var host = await IntegrationHost.StartAllAsync(extra: [new PartModule(part)]);
        var saved = await InScopeAsync(host.Services, sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.Egypt, "123456789", "0", "4711", "متجر", null)));
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Description : null);
        return (host, part);
    }

    private static async Task<List<Dictionary<string, object?>>> DocumentsAsync(IntegrationHost host)
    {
        await using var connection = await host.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Kind, SourceId, Reference, Number, PreviousHash, Hash, State, OriginalSourceId, OriginalReference, Total, StateReason FROM fis_Documents ORDER BY CreatedAt, Number";
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, i => reader.IsDBNull(i) ? null : reader.GetValue(i)));
        return rows;
    }

    [Fact]
    public async Task A_sale_and_its_return_each_get_their_document_chained_and_the_return_names_the_sale()
    {
        var (host, _) = await StartWithEgyptAsync();
        await using var _h = host;
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 3m);
        var sale = await CheckoutAsync(host.Services, cartId, tendered: 50m);
        Assert.True(sale.IsSuccess, sale.ErrorMessage);

        var line = (await InScopeAsync(host.Services, sp => sp.GetRequiredService<ISalesReader>().GetReturnableAsync(sale.SaleId)))!.Lines[0];
        var refund = await InScopeAsync(host.Services, sp => sp.GetRequiredService<RefundSaleCommandHandler>()
            .HandleAsync(new RefundSaleCommand(sale.SaleId, [new RefundLine(line.SaleItemId, 1m)], "Changed mind", POSPaymentMethod.Cash)));
        Assert.True(refund.IsSuccess, refund.IsFailure ? refund.Error.Description : null);

        var documents = await DocumentsAsync(host);
        Assert.Equal(2, documents.Count);
        var (saleDoc, returnDoc) = (documents[0], documents[1]);
        Assert.Equal((1L, (long)FiscalDocumentKind.Sale, "S-" + sale.SaleId.ToString("N")[..8].ToUpperInvariant(), 7.5d),
            ((long)saleDoc["Number"]!, (long)saleDoc["Kind"]!, (string)saleDoc["Reference"]!, Convert.ToDouble(saleDoc["Total"])));
        Assert.Equal((2L, (long)FiscalDocumentKind.Return, saleDoc["Hash"], 2.5d),
            ((long)returnDoc["Number"]!, (long)returnDoc["Kind"]!, returnDoc["PreviousHash"], Convert.ToDouble(returnDoc["Total"])));
        Assert.Equal(sale.SaleId.ToString().ToUpperInvariant(), ((string)returnDoc["OriginalSourceId"]!).ToUpperInvariant());
        Assert.Equal(saleDoc["Reference"], returnDoc["OriginalReference"]);
        Assert.All(documents, d => Assert.Equal((long)FiscalDocumentState.Waiting, (long)d["State"]!));
    }

    [Fact]
    public async Task When_the_document_cannot_be_saved_the_sale_is_not_saved_either()
    {
        var (host, _) = await StartWithEgyptAsync();
        await using var _h = host;
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 2m);

        await using (await FailInsertsAsync(host, "fis_Documents"))
        {
            var failed = await CheckoutAsync(host.Services, cartId, tendered: 50m);
            Assert.False(failed.IsSuccess);
            Assert.Equal("POS.Checkout.FiscalFailed", failed.ErrorCode);
        }

        Assert.Equal(0, await CountAsync(host, "sal_Sales", "Status = 3"));          // no completed sale
        Assert.Equal(10m, await OnHandAsync(host, shop.ProductId));                   // no stock issued
        Assert.Empty(await DocumentsAsync(host));

        var again = await CheckoutAsync(host.Services, cartId, tendered: 50m);       // works again once the database does
        Assert.True(again.IsSuccess, again.ErrorMessage);
        Assert.Single(await DocumentsAsync(host));
    }

    [Fact]
    public async Task A_document_the_part_cannot_build_leaves_the_sale_standing_and_tells_the_cashier()
    {
        var (host, part) = await StartWithEgyptAsync();
        await using var _h = host;
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);
        part.Refuse = "'Test product' has no item code from the tax authority.";

        var sale = await CheckoutAsync(host.Services, cartId, tendered: 50m);

        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        var notice = Assert.Single(sale.HardwareNotices!, n => n.Device == "electronic receipt");
        Assert.Contains("has no item code", notice.Message);
        var document = Assert.Single(await DocumentsAsync(host));
        Assert.Equal(((long)FiscalDocumentState.NotBuilt, (object?)null), ((long)document["State"]!, document["Number"]));
        Assert.Equal(1, await CountAsync(host, "sal_Sales", "Status = 3"));
    }

    [Fact]
    public async Task With_receipts_off_a_sale_makes_no_document()
    {
        await using var host = await IntegrationHost.StartAllAsync(extra: [new PartModule(new StandInPart())]);
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);

        Assert.True((await CheckoutAsync(host.Services, cartId, tendered: 50m)).IsSuccess);
        Assert.Empty(await DocumentsAsync(host));
    }
}
