using Catalog.Contracts.Interfaces;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Purchasing.Application.Commands;
using Purchasing.Application.Queries;
using Purchasing.Application.Security;
using Purchasing.Infrastructure.DependencyInjection;
using Purchasing.Infrastructure.Persistence;
using Purchasing.Tests.Application;
using Suppliers.Contracts.Interfaces;
using Tests.Common;
using Tests.Common.Security;

namespace Purchasing.Tests.Security;

public sealed class PurchasingAuthorizationTests
{
    private static async Task<(TestModuleDatabase<PurchasingDbContext> Db, ScriptedAuthorizationService Auth, StubSuppliers Suppliers, StubReceipts Receipts)> StartAsync(params string[] allowed)
    {
        var auth = new ScriptedAuthorizationService(allowed);
        var suppliers = new StubSuppliers();
        var receipts = new StubReceipts();
        var db = await TestModuleDatabase<PurchasingDbContext>.CreateAsync(s =>
        {
            s.AddPurchasingCore();
            s.AddSingleton<ISupplierLookup>(suppliers);
            s.AddSingleton<IProductLookup>(new StubCatalog());
            s.AddSingleton<IStockReceiptService>(receipts);
            s.AddSingleton<IAuthorizationService>(auth);
        });
        return (db, auth, suppliers, receipts);
    }

    [Fact]
    public async Task Every_purchasing_command_is_refused_without_its_capability_and_changes_nothing()
    {
        var (db, auth, suppliers, receipts) = await StartAsync();
        await using var _ = db;
        var supplier = suppliers.Register();
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var order = Guid.NewGuid();

        var results = new (string Name, Result Result, string Capability)[]
        {
            ("create", await sp.GetRequiredService<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(supplier)), PurchasingCapabilities.EditOrder),
            ("add line", await sp.GetRequiredService<AddPurchaseOrderLineCommandHandler>().HandleAsync(new AddPurchaseOrderLineCommand(order, "SKU-1", 1m)), PurchasingCapabilities.EditOrder),
            ("remove line", await sp.GetRequiredService<RemovePurchaseOrderLineCommandHandler>().HandleAsync(new RemovePurchaseOrderLineCommand(order, Guid.NewGuid())), PurchasingCapabilities.EditOrder),
            ("change qty", await sp.GetRequiredService<ChangePurchaseOrderLineQuantityCommandHandler>().HandleAsync(new ChangePurchaseOrderLineQuantityCommand(order, Guid.NewGuid(), 2m)), PurchasingCapabilities.EditOrder),
            ("submit", await sp.GetRequiredService<SubmitPurchaseOrderCommandHandler>().HandleAsync(new SubmitPurchaseOrderCommand(order)), PurchasingCapabilities.SubmitOrder),
            ("cancel", await sp.GetRequiredService<CancelPurchaseOrderCommandHandler>().HandleAsync(new CancelPurchaseOrderCommand(order, "no")), PurchasingCapabilities.CancelOrder),
            ("receive", await sp.GetRequiredService<ReceivePurchaseOrderCommandHandler>().HandleAsync(new ReceivePurchaseOrderCommand(order, Guid.NewGuid())), PurchasingCapabilities.ReceiveOrder)
        };

        foreach (var (name, result, capability) in results)
        {
            Assert.True(result.IsFailure, name);
            Assert.Equal(SecurityErrors.ForbiddenCode, result.Error.Code);
            Assert.Contains(capability, auth.Asked);
        }

        Assert.Empty(receipts.Received);
        Assert.Empty((await sp.GetRequiredService<ListPurchaseOrdersQueryHandler>().HandleAsync(new ListPurchaseOrdersQuery())).Items);
    }

    [Fact]
    public async Task With_the_capability_an_order_can_be_created()
    {
        var (db, _, suppliers, _) = await StartAsync(PurchasingCapabilities.EditOrder);
        await using var _2 = db;
        var supplier = suppliers.Register();
        using var scope = db.CreateScope();

        var created = await scope.ServiceProvider.GetRequiredService<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(supplier));

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.ToString() : null);
    }

    [Fact]
    public void Every_capability_is_declared_once_and_owned_by_purchasing()
    {
        var catalog = new CapabilityCatalog([new PurchasingCapabilityProvider()]);

        Assert.Equal(PurchasingCapabilities.All.Count, catalog.All.Count);
        Assert.All(catalog.All, c => Assert.Equal("purchasing", c.Module));
        Assert.True(catalog.Find(PurchasingCapabilities.ReceiveOrder)!.IsSensitive);
    }
}
