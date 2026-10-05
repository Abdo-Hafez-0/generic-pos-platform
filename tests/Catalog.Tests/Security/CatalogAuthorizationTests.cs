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

    [Fact]
    public void Every_capability_is_declared_once_and_owned_by_catalog()
    {
        var catalog = new CapabilityCatalog([new CatalogCapabilityProvider()]);

        Assert.Equal(CatalogCapabilities.All.Count, catalog.All.Count);
        Assert.All(catalog.All, c => Assert.Equal("catalog", c.Module));
    }
}
