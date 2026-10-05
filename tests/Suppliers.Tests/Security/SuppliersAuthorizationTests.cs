using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Suppliers.Application.Commands;
using Suppliers.Application.Queries;
using Suppliers.Application.Security;
using Suppliers.Infrastructure.DependencyInjection;
using Suppliers.Infrastructure.Persistence;
using Tests.Common;
using Tests.Common.Security;

namespace Suppliers.Tests.Security;

public sealed class SuppliersAuthorizationTests
{
    private static async Task<(TestModuleDatabase<SuppliersDbContext> Db, ScriptedAuthorizationService Auth)> StartAsync(params string[] allowed)
    {
        var auth = new ScriptedAuthorizationService(allowed);
        var db = await TestModuleDatabase<SuppliersDbContext>.CreateAsync(s =>
        {
            s.AddSuppliersCore();
            s.AddSingleton<IAuthorizationService>(auth);
        });
        return (db, auth);
    }

    [Fact]
    public async Task Supplier_commands_are_refused_without_the_capability_and_store_nothing()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var id = Guid.NewGuid();

        var results = new (string Name, Result Result)[]
        {
            ("create", await sp.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("S-1", "Acme"))),
            ("update", await sp.GetRequiredService<UpdateSupplierCommandHandler>().HandleAsync(new UpdateSupplierCommand(id, "Acme", null, null, null))),
            ("deactivate", await sp.GetRequiredService<DeactivateSupplierCommandHandler>().HandleAsync(new DeactivateSupplierCommand(id))),
            ("reactivate", await sp.GetRequiredService<ReactivateSupplierCommandHandler>().HandleAsync(new ReactivateSupplierCommand(id))),
            ("remove address", await sp.GetRequiredService<RemoveSupplierAddressCommandHandler>().HandleAsync(new RemoveSupplierAddressCommand(id, Guid.NewGuid()))),
            ("add contact", await sp.GetRequiredService<AddSupplierContactCommandHandler>().HandleAsync(new AddSupplierContactCommand(id, "Bob", null, null, null))),
            ("remove contact", await sp.GetRequiredService<RemoveSupplierContactCommandHandler>().HandleAsync(new RemoveSupplierContactCommand(id, Guid.NewGuid())))
        };

        foreach (var (name, result) in results)
        {
            Assert.True(result.IsFailure, name);
            Assert.Equal(SecurityErrors.ForbiddenCode, result.Error.Code);
        }

        Assert.All(auth.Asked, c => Assert.Equal(SuppliersCapabilities.ManageSuppliers, c));
        Assert.Equal(0, (await sp.GetRequiredService<ListSuppliersQueryHandler>().HandleAsync(new ListSuppliersQuery())).Total);
    }

    [Fact]
    public async Task With_the_capability_a_supplier_is_created()
    {
        var (db, _) = await StartAsync(SuppliersCapabilities.ManageSuppliers);
        await using var _2 = db;
        using var scope = db.CreateScope();

        var created = await scope.ServiceProvider.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("S-1", "Acme"));

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.ToString() : null);
    }

    [Fact]
    public void The_capability_is_declared_once_and_owned_by_suppliers()
    {
        var catalog = new CapabilityCatalog([new SuppliersCapabilityProvider()]);

        Assert.Equal(SuppliersCapabilities.All.Count, catalog.All.Count);
        Assert.All(catalog.All, c => Assert.Equal("suppliers", c.Module));
    }
}
