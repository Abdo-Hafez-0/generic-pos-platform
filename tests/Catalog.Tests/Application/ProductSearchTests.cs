using Catalog.Application.Commands;
using Catalog.Application.Queries;
using Catalog.Application.Security;
using Microsoft.Extensions.DependencyInjection;
using Tests.Common.Security;

namespace Catalog.Tests.Application;

/// <summary>FIX-01c: the product search behind the products screen.</summary>
public sealed class ProductSearchTests
{
    private static async Task<(CatalogTestDatabase Db, Guid Category, Guid Unit)> StartAsync(ScriptedAuthorizationService? authorization = null)
    {
        var db = await CatalogTestDatabase.CreateAsync(authorization ?? new ScriptedAuthorizationService(
            CatalogCapabilities.CreateProduct, CatalogCapabilities.ManageCategories, CatalogCapabilities.ManageUnits,
            CatalogCapabilities.EditProduct, CatalogCapabilities.DeactivateProduct, CatalogCapabilities.ViewCost));
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var category = await sp.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Drinks"));
        var unit = await sp.GetRequiredService<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand("Piece", "pc"));
        return (db, category.Value.Value, unit.Value.Value);
    }

    private static async Task<Guid> AddAsync(CatalogTestDatabase db, Guid category, Guid unit, string sku, string name, decimal? cost = null)
    {
        using var scope = db.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<CreateProductCommandHandler>()
            .HandleAsync(new CreateProductCommand(sku, name, category, unit, 2.5m, cost));
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Description : null);
        return created.Value.Value;
    }

    private static async Task<ProductSearchResult> SearchAsync(CatalogTestDatabase db, string? text, bool includeInactive = false)
    {
        using var scope = db.CreateScope();
        var found = await scope.ServiceProvider.GetRequiredService<SearchProductsQueryHandler>().HandleAsync(new SearchProductsQuery(text, includeInactive));
        Assert.True(found.IsSuccess);
        return found.Value;
    }

    [Fact]
    public async Task An_empty_search_lists_the_active_products_by_name_with_category_and_unit()
    {
        var (db, category, unit) = await StartAsync();
        await using var _ = db;
        await AddAsync(db, category, unit, "WATER-1", "Water");
        await AddAsync(db, category, unit, "COLA-1", "Cola");

        var result = await SearchAsync(db, null);

        Assert.Equal(["Cola", "Water"], result.Items.Select(p => p.Name));
        Assert.Equal(("Drinks", "Piece", "pc"), (result.Items[0].CategoryName, result.Items[0].UnitName, result.Items[0].UnitAbbreviation));
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public async Task The_search_matches_part_of_the_name_or_sku_ignoring_case_and_an_exact_barcode()
    {
        var (db, category, unit) = await StartAsync();
        await using var _ = db;
        var cola = await AddAsync(db, category, unit, "COLA-1", "Cola 330ml");
        await AddAsync(db, category, unit, "WATER-1", "Water");
        using (var scope = db.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<AssignBarcodeCommandHandler>()
                .HandleAsync(new AssignBarcodeCommand(cola, "4006381333931"))).IsSuccess);

        Assert.Equal(["Cola 330ml"], (await SearchAsync(db, "cola")).Items.Select(p => p.Name));
        Assert.Equal(["Water"], (await SearchAsync(db, "ter-")).Items.Select(p => p.Name));
        Assert.Equal(["Cola 330ml"], (await SearchAsync(db, " 4006381333931 ")).Items.Select(p => p.Name));
        Assert.Empty((await SearchAsync(db, "400638133393")).Items);   // part of a barcode is not a barcode
    }

    [Fact]
    public async Task Wildcards_in_the_search_are_literal()
    {
        var (db, category, unit) = await StartAsync();
        await using var _ = db;
        await AddAsync(db, category, unit, "A-1", "Juice 100%");
        await AddAsync(db, category, unit, "B-1", "Bread");

        Assert.Equal(["Juice 100%"], (await SearchAsync(db, "%")).Items.Select(p => p.Name));
        Assert.Empty((await SearchAsync(db, "_r_")).Items);
    }

    [Fact]
    public async Task Deactivated_products_appear_only_when_asked_for()
    {
        var (db, category, unit) = await StartAsync();
        await using var _ = db;
        var old = await AddAsync(db, category, unit, "OLD-1", "Old cola");
        using (var scope = db.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<DeactivateProductCommandHandler>().HandleAsync(new DeactivateProductCommand(old))).IsSuccess);

        Assert.Empty((await SearchAsync(db, "cola")).Items);
        Assert.Single((await SearchAsync(db, "cola", includeInactive: true)).Items);
    }

    [Fact]
    public async Task The_cost_price_is_hidden_from_users_without_catalog_cost_view()
    {
        var auth = new ScriptedAuthorizationService(CatalogCapabilities.CreateProduct, CatalogCapabilities.ManageCategories, CatalogCapabilities.ManageUnits, CatalogCapabilities.ViewCost);
        var (db, category, unit) = await StartAsync(auth);
        await using var _ = db;
        await AddAsync(db, category, unit, "COLA-1", "Cola", cost: 1.1m);

        Assert.Equal(1.1m, Assert.Single((await SearchAsync(db, null)).Items).CostPrice);
        auth.Allowed.Remove(CatalogCapabilities.ViewCost);
        var hidden = Assert.Single((await SearchAsync(db, null)).Items);
        Assert.Null(hidden.CostPrice);
        Assert.Equal(2.5m, hidden.SalePrice);
    }

    [Fact]
    public async Task More_results_than_the_limit_are_cut_and_flagged()
    {
        var (db, category, unit) = await StartAsync();
        await using var _ = db;
        using (var scope = db.CreateScope())
        {
            var create = scope.ServiceProvider.GetRequiredService<CreateProductCommandHandler>();
            for (var i = 0; i <= SearchProductsQuery.MaxResults; i++)
                Assert.True((await create.HandleAsync(new CreateProductCommand($"P-{i:D4}", $"Product {i:D4}", category, unit, 1m))).IsSuccess);
        }

        var result = await SearchAsync(db, null);

        Assert.Equal(SearchProductsQuery.MaxResults, result.Items.Count);
        Assert.True(result.IsTruncated);
    }
}
