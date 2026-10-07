using Catalog.Application.Commands;
using Catalog.Application.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace Catalog.Tests.Application;

/// <summary>FIX-01c: a barcode identifies one product, so a scan at the till can never match two.</summary>
public sealed class BarcodeUniquenessTests
{
    [Fact]
    public async Task A_barcode_carried_by_another_product_is_refused_with_the_owner_named()
    {
        await using var db = await CatalogTestDatabase.CreateAsync();
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var category = (await sp.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Drinks"))).Value.Value;
        var unit = (await sp.GetRequiredService<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand("Piece", "pc"))).Value.Value;
        var create = sp.GetRequiredService<CreateProductCommandHandler>();
        var cola = (await create.HandleAsync(new CreateProductCommand("COLA-1", "Cola", category, unit, 2m))).Value.Value;
        var water = (await create.HandleAsync(new CreateProductCommand("WATER-1", "Water", category, unit, 1m))).Value.Value;
        var assign = sp.GetRequiredService<AssignBarcodeCommandHandler>();
        Assert.True((await assign.HandleAsync(new AssignBarcodeCommand(cola, "4006381333931"))).IsSuccess);

        var refused = await assign.HandleAsync(new AssignBarcodeCommand(water, " 4006381333931 "));

        Assert.True(refused.IsFailure);
        Assert.Equal("Catalog.Barcode.InUse", refused.Error.Code);
        Assert.Contains("COLA-1", refused.Error.Description);
        var scanned = await sp.GetRequiredService<FindProductByBarcodeQueryHandler>().HandleAsync(new FindProductByBarcodeQuery("4006381333931"));
        Assert.Equal(cola, scanned.Value.ProductId);
    }
}
