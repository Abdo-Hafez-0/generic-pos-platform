using Customers.Application.Commands;
using Customers.Application.Queries;
using Customers.Contracts.Interfaces;
using Customers.Contracts.Models;
using Customers.Domain.Enums;
using Customers.Infrastructure.DependencyInjection;
using Customers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tests.Common;

namespace Customers.Tests.Application;

/// <summary>Handlers, repositories and contract services against in-memory SQLite (schema from the EF model).</summary>
public sealed class CustomerApplicationTests
{
    private static Task<TestModuleDatabase<CustomersDbContext>> NewDb()
        => TestModuleDatabase<CustomersDbContext>.CreateAsync(s => s.AddCustomersCore());

    private static async Task<Guid> Create(TestModuleDatabase<CustomersDbContext> db, string code = "C1", string name = "Acme", string? email = "a@acme.test")
        => await db.InScopeAsync(async sp =>
        {
            var r = await sp.GetRequiredService<CreateCustomerCommandHandler>().HandleAsync(new CreateCustomerCommand(code, name, email, "555"));
            Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
            return r.Value;
        });

    private static Task<T> Q<T>(TestModuleDatabase<CustomersDbContext> db, Func<IServiceProvider, Task<T>> f) => db.InScopeAsync(f);

    /// <summary>The read handlers return Result (customers.customer.view); these business tests run with a permissive authorization.</summary>
    private static async Task<T> Q<T>(TestModuleDatabase<CustomersDbContext> db, Func<IServiceProvider, Task<Platform.Core.Results.Result<T>>> f)
    {
        var result = await db.InScopeAsync(f);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    [Fact]
    public async Task Create_PersistsCustomer_AndGetReturnsIt()
    {
        await using var db = await NewDb();
        var id = await Create(db);

        var dto = await Q(db, sp => sp.GetRequiredService<GetCustomerByIdQueryHandler>().HandleAsync(new GetCustomerByIdQuery(id)));

        Assert.NotNull(dto);
        Assert.Equal("C1", dto!.Code);
        Assert.Equal("Acme", dto.Name);
        Assert.Equal(CustomerStatus.Active, dto.Status);
        Assert.Empty(dto.Addresses);
    }

    [Fact]
    public async Task Create_DuplicateCode_IsRejected_CaseInsensitively()
    {
        await using var db = await NewDb();
        await Create(db, "abc");

        var r = await db.InScopeAsync(sp => sp.GetRequiredService<CreateCustomerCommandHandler>().HandleAsync(new CreateCustomerCommand("ABC", "Other")));

        Assert.True(r.IsFailure);
        Assert.Equal("Customers.CreateCustomer.DuplicateCode", r.Error.Code);
    }

    [Fact]
    public async Task Create_InvalidInput_Fails_AndPersistsNothing()
    {
        await using var db = await NewDb();

        var r = await db.InScopeAsync(sp => sp.GetRequiredService<CreateCustomerCommandHandler>().HandleAsync(new CreateCustomerCommand("", "x")));
        var page = await Q(db, sp => sp.GetRequiredService<ListCustomersQueryHandler>().HandleAsync(new ListCustomersQuery()));

        Assert.Equal("Customers.Customer.CodeRequired", r.Error.Code);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task Update_ChangesFields_AndUnknownCustomerFails()
    {
        await using var db = await NewDb();
        var id = await Create(db);

        var ok = await db.InScopeAsync(sp => sp.GetRequiredService<UpdateCustomerCommandHandler>().HandleAsync(new UpdateCustomerCommand(id, "Renamed", null, "999", "note")));
        var missing = await db.InScopeAsync(sp => sp.GetRequiredService<UpdateCustomerCommandHandler>().HandleAsync(new UpdateCustomerCommand(Guid.NewGuid(), "x", null, null, null)));
        var dto = await Q(db, sp => sp.GetRequiredService<GetCustomerByIdQueryHandler>().HandleAsync(new GetCustomerByIdQuery(id)));

        Assert.True(ok.IsSuccess);
        Assert.Equal("Customers.UpdateCustomer.CustomerNotFound", missing.Error.Code);
        Assert.Equal("Renamed", dto!.Name);
        Assert.Null(dto.Email);
        Assert.Equal("999", dto.Phone);
    }

    [Fact]
    public async Task Deactivate_Reactivate_Roundtrip_AndInactiveCustomersCannotBeEdited()
    {
        await using var db = await NewDb();
        var id = await Create(db);

        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateCustomerCommandHandler>().HandleAsync(new DeactivateCustomerCommand(id)))).IsSuccess);
        var edit = await db.InScopeAsync(sp => sp.GetRequiredService<UpdateCustomerCommandHandler>().HandleAsync(new UpdateCustomerCommand(id, "x", null, null, null)));
        var twice = await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateCustomerCommandHandler>().HandleAsync(new DeactivateCustomerCommand(id)));
        Assert.Equal("Customers.Customer.Inactive", edit.Error.Code);
        Assert.Equal("Customers.Customer.AlreadyInactive", twice.Error.Code);
        Assert.Equal(CustomerStatus.Inactive, (await Q(db, sp => sp.GetRequiredService<GetCustomerByIdQueryHandler>().HandleAsync(new GetCustomerByIdQuery(id))))!.Status);

        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<ReactivateCustomerCommandHandler>().HandleAsync(new ReactivateCustomerCommand(id)))).IsSuccess);
        Assert.Equal(CustomerStatus.Active, (await Q(db, sp => sp.GetRequiredService<GetCustomerByIdQueryHandler>().HandleAsync(new GetCustomerByIdQuery(id))))!.Status);
    }

    [Fact]
    public async Task Deactivate_UnknownCustomer_Fails()
    {
        await using var db = await NewDb();

        var r = await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateCustomerCommandHandler>().HandleAsync(new DeactivateCustomerCommand(Guid.NewGuid())));

        Assert.Equal("Customers.DeactivateCustomer.CustomerNotFound", r.Error.Code);
    }

    [Fact]
    public async Task AddressesAndContacts_ArePersisted_AndRemovable()
    {
        await using var db = await NewDb();
        var id = await Create(db);

        var addressId = (await db.InScopeAsync(sp => sp.GetRequiredService<AddCustomerAddressCommandHandler>()
            .HandleAsync(new AddCustomerAddressCommand(id, AddressType.Billing, "1 Main", null, "Town", null, "12345", "USA")))).Value;
        var contactId = (await db.InScopeAsync(sp => sp.GetRequiredService<AddCustomerContactCommandHandler>()
            .HandleAsync(new AddCustomerContactCommand(id, "Jane", "j@x.test", null, "Buyer")))).Value;

        var dto = (await Q(db, sp => sp.GetRequiredService<GetCustomerByIdQueryHandler>().HandleAsync(new GetCustomerByIdQuery(id))))!;
        Assert.Equal(addressId, Assert.Single(dto.Addresses).AddressId);
        Assert.Equal(contactId, Assert.Single(dto.Contacts).ContactId);

        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<RemoveCustomerAddressCommandHandler>().HandleAsync(new RemoveCustomerAddressCommand(id, addressId)))).IsSuccess);
        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<RemoveCustomerContactCommandHandler>().HandleAsync(new RemoveCustomerContactCommand(id, contactId)))).IsSuccess);
        dto = (await Q(db, sp => sp.GetRequiredService<GetCustomerByIdQueryHandler>().HandleAsync(new GetCustomerByIdQuery(id))))!;
        Assert.Empty(dto.Addresses);
        Assert.Empty(dto.Contacts);
    }

    [Fact]
    public async Task AddAddress_ToUnknownCustomer_OrWithBadData_Fails()
    {
        await using var db = await NewDb();
        var id = await Create(db);

        var missing = await db.InScopeAsync(sp => sp.GetRequiredService<AddCustomerAddressCommandHandler>()
            .HandleAsync(new AddCustomerAddressCommand(Guid.NewGuid(), AddressType.Billing, "1", null, "T", null, null, "X")));
        var bad = await db.InScopeAsync(sp => sp.GetRequiredService<AddCustomerAddressCommandHandler>()
            .HandleAsync(new AddCustomerAddressCommand(id, AddressType.Billing, "", null, "T", null, null, "X")));

        Assert.Equal("Customers.AddAddress.CustomerNotFound", missing.Error.Code);
        Assert.Equal("Customers.Address.Line1Required", bad.Error.Code);
    }

    [Fact]
    public async Task List_IsPaged_Ordered_AndFilterable()
    {
        await using var db = await NewDb();
        foreach (var (code, name) in new[] { ("C3", "Charlie"), ("C1", "Alice"), ("C2", "Bob"), ("C4", "Dave") })
            await Create(db, code, name);
        var bob = (await Q(db, sp => sp.GetRequiredService<SearchCustomersQueryHandler>().HandleAsync(new SearchCustomersQuery("Bob")))).Single();
        await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateCustomerCommandHandler>().HandleAsync(new DeactivateCustomerCommand(bob.CustomerId)));

        var first = await Q(db, sp => sp.GetRequiredService<ListCustomersQueryHandler>().HandleAsync(new ListCustomersQuery(0, 2)));
        var second = await Q(db, sp => sp.GetRequiredService<ListCustomersQueryHandler>().HandleAsync(new ListCustomersQuery(2, 2)));
        var inactive = await Q(db, sp => sp.GetRequiredService<ListCustomersQueryHandler>().HandleAsync(new ListCustomersQuery(Status: CustomerStatus.Inactive)));

        Assert.Equal(4, first.Total);
        Assert.Equal(["Alice", "Bob"], first.Items.Select(i => i.Name).ToArray());
        Assert.Equal(["Charlie", "Dave"], second.Items.Select(i => i.Name).ToArray());
        Assert.Equal(["Bob"], inactive.Items.Select(i => i.Name).ToArray());
        Assert.Equal(1, inactive.Total);
    }

    [Fact]
    public async Task List_ClampsPageSize_AndNegativeSkip()
    {
        await using var db = await NewDb();
        await Create(db);

        var page = await Q(db, sp => sp.GetRequiredService<ListCustomersQueryHandler>().HandleAsync(new ListCustomersQuery(-5, 100000)));

        Assert.Equal(0, page.Skip);
        Assert.Equal(ListCustomersQueryHandler.MaxPageSize, page.Take);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Search_MatchesCodeNameEmailPhone_CaseInsensitively_AndEscapesWildcards()
    {
        await using var db = await NewDb();
        await Create(db, "ACME-1", "Acme Corporation", "sales@acme.test");
        await Create(db, "OTHER-2", "Other 100% Co", "x@other.test");

        Task<IReadOnlyList<Customers.Application.DTOs.CustomerListItemDto>> Search(string t)
            => Q(db, sp => sp.GetRequiredService<SearchCustomersQueryHandler>().HandleAsync(new SearchCustomersQuery(t)));

        Assert.Single(await Search("acme"));            // name/code/email
        Assert.Single(await Search("CORPORATION"));
        Assert.Single(await Search("sales@"));
        Assert.Equal(2, (await Search("555")).Count);   // phone
        Assert.Single(await Search("100%"));            // '%' is literal, not a wildcard
        Assert.Single(await Search("%"));               // matches only the name that really contains a percent sign
        Assert.Empty(await Search("   "));
        Assert.Empty(await Search("zzz"));
    }

    // ------------------------------------------------------------------ contract implementations

    [Fact]
    public async Task Contracts_Lookup_FindsByIdAndCode_AndReportsStatus()
    {
        await using var db = await NewDb();
        var id = await Create(db, "k-1", "Contract Co");

        var byId = await Q(db, sp => sp.GetRequiredService<ICustomerLookup>().FindByIdAsync(id));
        var byCode = await Q(db, sp => sp.GetRequiredService<ICustomerLookup>().FindByCodeAsync("K-1"));
        var none = await Q(db, sp => sp.GetRequiredService<ICustomerLookup>().FindByIdAsync(Guid.NewGuid()));
        var blank = await Q(db, sp => sp.GetRequiredService<ICustomerLookup>().FindByCodeAsync(" "));
        await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateCustomerCommandHandler>().HandleAsync(new DeactivateCustomerCommand(id)));
        var inactive = await Q(db, sp => sp.GetRequiredService<ICustomerLookup>().FindByIdAsync(id));

        Assert.Equal("Contract Co", byId!.Name);
        Assert.Equal(id, byCode!.CustomerId);
        Assert.Null(none);
        Assert.Null(blank);
        Assert.Equal(CustomerStatusContract.Active, byId.Status);
        Assert.Equal(CustomerStatusContract.Inactive, inactive!.Status);
    }

    [Fact]
    public async Task Contracts_Reader_SearchesAndSummarises()
    {
        await using var db = await NewDb();
        var a = await Create(db, "A", "Alpha");
        await Create(db, "B", "Beta");
        await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateCustomerCommandHandler>().HandleAsync(new DeactivateCustomerCommand(a)));

        var found = await Q(db, sp => sp.GetRequiredService<ICustomerReader>().SearchAsync("bet"));
        var summary = await Q(db, sp => sp.GetRequiredService<ICustomerReader>().GetSummaryAsync());

        Assert.Equal("Beta", Assert.Single(found).Name);
        Assert.Equal(new CustomerSummaryResult(2, 1, 1), summary);
        Assert.Empty(await Q(db, sp => sp.GetRequiredService<ICustomerReader>().SearchAsync("")));
    }

    [Fact]
    public async Task Contracts_DoNotExposeDomainTypes()
    {
        var assembly = typeof(ICustomerLookup).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Customers.Domain", StringComparison.Ordinal));
        foreach (var method in typeof(ICustomerLookup).GetMethods().Concat(typeof(ICustomerReader).GetMethods()))
            Assert.DoesNotContain("Customers.Domain", method.ReturnType.FullName ?? string.Empty);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Data_IsPersisted_AcrossScopes_ThroughTheUnitOfWork()
    {
        await using var db = await NewDb();
        var id = await Create(db);
        await db.InScopeAsync(sp => sp.GetRequiredService<AddCustomerContactCommandHandler>().HandleAsync(new AddCustomerContactCommand(id, "Jane", null, null, null)));

        var contacts = await db.InScopeAsync(sp => sp.GetRequiredService<CustomersDbContext>().CustomerContacts.CountAsync());

        Assert.Equal(1, contacts);
    }
}
