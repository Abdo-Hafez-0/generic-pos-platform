using Catalog.Application.Commands;
using Inventory.Application.Commands;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;

namespace Integration.Tests;

/// <summary>
/// Stage 9 offline-first proof. The desktop host runs the whole POS path - start, load modules, open POS, read the local catalog,
/// sell, update local stock, persist the sale - in a process where NO server code exists: no cloud API, no server database, no
/// server assembly is even loaded, and nothing is configured to reach a server. The cloud extends the platform; it is not a dependency.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ServerIndependenceTests
{
    private static readonly string[] ServerAssemblyPrefixes = ["Cloud.", "AdminPortal", "BackupServer", "LicenseServer", "UpdateServer"];

    [Fact]
    public async Task TheWholePosPath_Works_WithNoServerCodeInTheProcess()
    {
        await using var host = await IntegrationHost.StartAllAsync();

        Guid productId, warehouseId;
        using (var scope = host.Services.CreateScope())
        {
            var p = scope.ServiceProvider;
            var category = await p.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Drinks"));
            var unit = await p.GetRequiredService<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand("Piece", "pc"));
            var product = await p.GetRequiredService<CreateProductCommandHandler>()
                .HandleAsync(new CreateProductCommand("COLA-1", "Cola", category.Value.Value, unit.Value.Value, 2.5m, 1m));
            var warehouse = await p.GetRequiredService<CreateWarehouseCommandHandler>().HandleAsync(new CreateWarehouseCommand("Main", "MAIN"));
            var stocked = await p.GetRequiredService<AddStockCommandHandler>().HandleAsync(new AddStockCommand(product.Value.Value, warehouse.Value, 10m));
            Assert.True(product.IsSuccess && warehouse.IsSuccess && stocked.IsSuccess);
            productId = product.Value.Value;
            warehouseId = warehouse.Value;
        }

        using (var scope = host.Services.CreateScope())
        {
            var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
            var session = await pos.OpenSessionAsync("cashier-1", warehouseId);
            var cart = await pos.StartCartAsync(session.SessionId);
            var added = await pos.AddProductAsync(cart.CartId, "COLA-1", 3m);
            var checkout = await pos.CheckoutAsync(cart.CartId);

            Assert.True(added.IsSuccess, added.ErrorMessage);
            Assert.True(checkout.IsSuccess, checkout.ErrorMessage);

            var sale = await scope.ServiceProvider.GetRequiredService<ISalesReader>().FindByIdAsync(checkout.SaleId);
            Assert.Equal(SaleStatusContract.Completed, sale!.Status);

            var level = await scope.ServiceProvider.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(productId, warehouseId);
            Assert.Equal(7m, level!.OnHand);
        }

        var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").ToList();
        Assert.DoesNotContain(loaded, n => ServerAssemblyPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)));
    }

    [Fact]
    public void TheIntegrationHost_DoesNotReferenceAnyServerAssembly()
    {
        var referenced = typeof(ServerIndependenceTests).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "");

        Assert.DoesNotContain(referenced, n => ServerAssemblyPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)));
    }
}
