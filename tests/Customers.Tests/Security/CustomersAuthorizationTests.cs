using Customers.Application.Commands;
using Customers.Application.Queries;
using Customers.Application.Security;
using Customers.Domain.Enums;
using Customers.Infrastructure.DependencyInjection;
using Customers.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Tests.Common;
using Tests.Common.Security;

namespace Customers.Tests.Security;

public sealed class CustomersAuthorizationTests
{
    private static async Task<(TestModuleDatabase<CustomersDbContext> Db, ScriptedAuthorizationService Auth)> StartAsync(params string[] allowed)
    {
        var auth = new ScriptedAuthorizationService(allowed);
        var db = await TestModuleDatabase<CustomersDbContext>.CreateAsync(s =>
        {
            s.AddCustomersCore();
            s.AddSingleton<IAuthorizationService>(auth);
        });
        return (db, auth);
    }

    [Fact]
    public async Task Every_customer_command_is_refused_without_customers_customer_manage_and_stores_nothing()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var id = Guid.NewGuid();

        var results = new (string Name, Result Result)[]
        {
            ("create", await sp.GetRequiredService<CreateCustomerCommandHandler>().HandleAsync(new CreateCustomerCommand("C-1", "Ann"))),
            ("update", await sp.GetRequiredService<UpdateCustomerCommandHandler>().HandleAsync(new UpdateCustomerCommand(id, "Ann", null, null, null))),
            ("deactivate", await sp.GetRequiredService<DeactivateCustomerCommandHandler>().HandleAsync(new DeactivateCustomerCommand(id))),
            ("reactivate", await sp.GetRequiredService<ReactivateCustomerCommandHandler>().HandleAsync(new ReactivateCustomerCommand(id))),
            ("add address", await sp.GetRequiredService<AddCustomerAddressCommandHandler>().HandleAsync(new AddCustomerAddressCommand(id, AddressType.Billing, "1 Main St", null, "Town", null, null, "US"))),
            ("remove address", await sp.GetRequiredService<RemoveCustomerAddressCommandHandler>().HandleAsync(new RemoveCustomerAddressCommand(id, Guid.NewGuid()))),
            ("add contact", await sp.GetRequiredService<AddCustomerContactCommandHandler>().HandleAsync(new AddCustomerContactCommand(id, "Bob", null, null, null))),
            ("remove contact", await sp.GetRequiredService<RemoveCustomerContactCommandHandler>().HandleAsync(new RemoveCustomerContactCommand(id, Guid.NewGuid())))
        };

        foreach (var (name, result) in results)
        {
            Assert.True(result.IsFailure, name);
            Assert.Equal(SecurityErrors.ForbiddenCode, result.Error.Code);
        }

        Assert.All(auth.Asked, c => Assert.Equal(CustomersCapabilities.ManageCustomers, c));
        Assert.Equal(0, (await sp.GetRequiredService<ListCustomersQueryHandler>().HandleAsync(new ListCustomersQuery())).Total);
    }

    [Fact]
    public async Task With_the_capability_a_customer_is_created()
    {
        var (db, _) = await StartAsync(CustomersCapabilities.ManageCustomers);
        await using var _2 = db;
        using var scope = db.CreateScope();

        var created = await scope.ServiceProvider.GetRequiredService<CreateCustomerCommandHandler>().HandleAsync(new CreateCustomerCommand("C-1", "Ann"));

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.ToString() : null);
    }

    [Fact]
    public void The_capability_is_declared_once_and_owned_by_customers()
    {
        var catalog = new CapabilityCatalog([new CustomersCapabilityProvider()]);

        Assert.Equal(CustomersCapabilities.All.Count, catalog.All.Count);
        Assert.All(catalog.All, c => Assert.Equal("customers", c.Module));
    }
}
