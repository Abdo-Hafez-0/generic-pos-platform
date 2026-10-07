using Catalog.Application.Commands;
using Catalog.Application.Queries;
using Catalog.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Tests.Common.Security;

namespace Catalog.Tests.Security;

/// <summary>Catalog operations are refused by the handlers themselves - these tests call the handlers directly, with no UI in between.</summary>
public sealed class CatalogAuthorizationTests
{
    private static async Task<(CatalogTestDatabase Db, ScriptedAuthorizationService Auth)> StartAsync(params string[] allowed)
    {
        var auth = new ScriptedAuthorizationService(allowed);
        return (await CatalogTestDatabase.CreateAsync(auth), auth);
    }

    private static async Task<int> CategoryCountAsync(CatalogTestDatabase db)
    {
        using var scope = db.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<GetAllCategoriesQueryHandler>().HandleAsync(new GetAllCategoriesQuery())).Value.Count;
    }

    [Fact]
    public async Task Every_catalog_command_is_refused_without_its_capability()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var anyId = Guid.NewGuid();

        var results = new (string Name, Result Result, string Capability)[]
        {
            ("create category", await sp.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Drinks")), CatalogCapabilities.ManageCategories),
            ("create unit", await sp.GetRequiredService<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand("Piece", "pcs")), CatalogCapabilities.ManageUnits),
            ("create product", await sp.GetRequiredService<CreateProductCommandHandler>().HandleAsync(new CreateProductCommand("SKU-1", "Cola", anyId, anyId, 1m)), CatalogCapabilities.CreateProduct),
            ("deactivate", await sp.GetRequiredService<DeactivateProductCommandHandler>().HandleAsync(new DeactivateProductCommand(anyId)), CatalogCapabilities.DeactivateProduct)
        };

        foreach (var (name, result, capability) in results)
        {
            Assert.True(result.IsFailure, name);
            Assert.Equal(SecurityErrors.ForbiddenCode, result.Error.Code);
            Assert.Contains(capability, auth.Asked);
        }

        Assert.Equal(0, await CategoryCountAsync(db));
    }

    [Fact]
    public async Task Update_and_barcode_assignment_need_catalog_product_edit()
    {
        var (db, auth) = await StartAsync(CatalogCapabilities.CreateProduct);
        await using var _ = db;
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var update = await sp.GetRequiredService<UpdateProductCommandHandler>().HandleAsync(
            new UpdateProductCommand(Guid.NewGuid(), "New name", Guid.NewGuid(), Guid.NewGuid(), 5m));
        var barcode = await sp.GetRequiredService<AssignBarcodeCommandHandler>().HandleAsync(
            new AssignBarcodeCommand(Guid.NewGuid(), "123456789012"));

        Assert.Equal(SecurityErrors.ForbiddenCode, update.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, barcode.Error.Code);
        Assert.Equal(2, auth.Asked.Count(c => c == CatalogCapabilities.EditProduct));
    }

    [Fact]
    public async Task Holding_the_capability_lets_the_operation_through_and_it_works_as_before()
    {
        var (db, auth) = await StartAsync(CatalogCapabilities.ManageCategories);
        await using var _ = db;
        using var scope = db.CreateScope();

        var created = await scope.ServiceProvider.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Drinks"));

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.ToString() : null);
        Assert.Equal(1, await CategoryCountAsync(db));
    }

    private static async Task<Guid> SeedProductAsync(CatalogTestDatabase db, ScriptedAuthorizationService auth)
    {
        auth.Allowed.UnionWith([CatalogCapabilities.ManageCategories, CatalogCapabilities.ManageUnits, CatalogCapabilities.CreateProduct, CatalogCapabilities.EditProduct]);
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var category = (await sp.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Drinks"))).Value.Value;
        var unit = (await sp.GetRequiredService<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand("Piece", "pcs"))).Value.Value;
        var product = (await sp.GetRequiredService<CreateProductCommandHandler>().HandleAsync(new CreateProductCommand("COLA", "Cola", category, unit, 2.5m, 1.1m))).Value.Value;
        await sp.GetRequiredService<AssignBarcodeCommandHandler>().HandleAsync(new AssignBarcodeCommand(product, "4006381333931"));
        auth.Allowed.Clear();
        return product;
    }

    [Fact]
    public async Task Product_reads_carry_no_cost_price_without_catalog_cost_view_but_are_not_refused()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        var product = await SeedProductAsync(db, auth);
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var byId = await sp.GetRequiredService<GetProductByIdQueryHandler>().HandleAsync(new GetProductByIdQuery(product));
        var bySku = await sp.GetRequiredService<GetProductBySkuQueryHandler>().HandleAsync(new GetProductBySkuQuery("COLA"));
        var byBarcode = await sp.GetRequiredService<FindProductByBarcodeQueryHandler>().HandleAsync(new FindProductByBarcodeQuery("4006381333931"));

        Assert.True(byId.IsSuccess && bySku.IsSuccess && byBarcode.IsSuccess);
        Assert.Null(byId.Value.CostPrice);
        Assert.Null(bySku.Value.CostPrice);
        Assert.Null(byBarcode.Value.CostPrice);
        Assert.Equal(2.5m, byId.Value.SalePrice);
    }

    [Fact]
    public async Task Holders_of_catalog_cost_view_see_the_cost_price()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        var product = await SeedProductAsync(db, auth);
        auth.Allowed.Add(CatalogCapabilities.ViewCost);
        using var scope = db.CreateScope();

        var byId = await scope.ServiceProvider.GetRequiredService<GetProductByIdQueryHandler>().HandleAsync(new GetProductByIdQuery(product));
        var byBarcode = await scope.ServiceProvider.GetRequiredService<FindProductByBarcodeQueryHandler>().HandleAsync(new FindProductByBarcodeQuery("4006381333931"));

        Assert.Equal(1.1m, byId.Value.CostPrice);
        Assert.Equal(1.1m, byBarcode.Value.CostPrice);
    }

    [Fact]
    public async Task Editing_a_product_without_catalog_cost_view_keeps_the_cost_price_it_could_not_see()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        var product = await SeedProductAsync(db, auth);   // stored cost 1.1
        auth.Allowed.Add(CatalogCapabilities.EditProduct);
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var seen = (await sp.GetRequiredService<GetProductByIdQueryHandler>().HandleAsync(new GetProductByIdQuery(product))).Value;
        Assert.Null(seen.CostPrice);

        // the screen sends back what it was shown (no cost) together with a new name and price
        var edited = await sp.GetRequiredService<UpdateProductCommandHandler>().HandleAsync(
            new UpdateProductCommand(product, "Cola 330ml", seen.CategoryId, seen.UnitId, 2.75m, seen.CostPrice, seen.Description));
        Assert.True(edited.IsSuccess);

        auth.Allowed.Add(CatalogCapabilities.ViewCost);
        using var read = db.CreateScope();
        var after = (await read.ServiceProvider.GetRequiredService<GetProductByIdQueryHandler>().HandleAsync(new GetProductByIdQuery(product))).Value;
        Assert.Equal(("Cola 330ml", 2.75m, 1.1m), (after.Name, after.SalePrice, after.CostPrice));
    }

    [Fact]
    public async Task Holders_of_catalog_cost_view_can_change_or_clear_the_cost_price()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        var product = await SeedProductAsync(db, auth);
        auth.Allowed.UnionWith([CatalogCapabilities.EditProduct, CatalogCapabilities.ViewCost]);
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var seen = (await sp.GetRequiredService<GetProductByIdQueryHandler>().HandleAsync(new GetProductByIdQuery(product))).Value;

        Assert.True((await sp.GetRequiredService<UpdateProductCommandHandler>().HandleAsync(
            new UpdateProductCommand(product, seen.Name, seen.CategoryId, seen.UnitId, seen.SalePrice, null, seen.Description))).IsSuccess);

        using var read = db.CreateScope();
        Assert.Null((await read.ServiceProvider.GetRequiredService<GetProductByIdQueryHandler>().HandleAsync(new GetProductByIdQuery(product))).Value.CostPrice);
    }

    [Fact]
    public void Every_capability_is_declared_once_and_owned_by_catalog()
    {
        var catalog = new CapabilityCatalog([new CatalogCapabilityProvider()]);

        Assert.Equal(CatalogCapabilities.All.Count, catalog.All.Count);
        Assert.All(catalog.All, c => Assert.Equal("catalog", c.Module));
    }
}
