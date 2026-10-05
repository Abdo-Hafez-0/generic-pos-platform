using Suppliers.Application.Commands;
using Suppliers.Application.Queries;
using Suppliers.Contracts.Interfaces;
using Suppliers.Contracts.Models;
using Suppliers.Domain.Enums;
using Suppliers.Infrastructure.DependencyInjection;
using Suppliers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tests.Common;

namespace Suppliers.Tests.Application;

/// <summary>Handlers, repositories and contract services against in-memory SQLite (schema from the EF model).</summary>
public sealed class SupplierApplicationTests
{
    private static Task<TestModuleDatabase<SuppliersDbContext>> NewDb()
        => TestModuleDatabase<SuppliersDbContext>.CreateAsync(s => s.AddSuppliersCore());

    private static async Task<Guid> Create(TestModuleDatabase<SuppliersDbContext> db, string code = "C1", string name = "Acme", string? email = "a@acme.test")
        => await db.InScopeAsync(async sp =>
        {
            var r = await sp.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand(code, name, email, "555"));
            Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
            return r.Value;
        });

    private static Task<T> Q<T>(TestModuleDatabase<SuppliersDbContext> db, Func<IServiceProvider, Task<T>> f) => db.InScopeAsync(f);

    [Fact]
    public async Task Create_PersistsSupplier_AndGetReturnsIt()
    {
        await using var db = await NewDb();
        var id = await Create(db);

        var dto = await Q(db, sp => sp.GetRequiredService<GetSupplierByIdQueryHandler>().HandleAsync(new GetSupplierByIdQuery(id)));

        Assert.NotNull(dto);
        Assert.Equal("C1", dto!.Code);
        Assert.Equal("Acme", dto.Name);
        Assert.Equal(SupplierStatus.Active, dto.Status);
        Assert.Empty(dto.Addresses);
    }

    [Fact]
    public async Task Create_DuplicateCode_IsRejected_CaseInsensitively()
    {
        await using var db = await NewDb();
        await Create(db, "abc");

        var r = await db.InScopeAsync(sp => sp.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("ABC", "Other")));

        Assert.True(r.IsFailure);
        Assert.Equal("Suppliers.CreateSupplier.DuplicateCode", r.Error.Code);
    }

    [Fact]
    public async Task Create_InvalidInput_Fails_AndPersistsNothing()
    {
        await using var db = await NewDb();

        var r = await db.InScopeAsync(sp => sp.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("", "x")));
        var page = await Q(db, sp => sp.GetRequiredService<ListSuppliersQueryHandler>().HandleAsync(new ListSuppliersQuery()));

        Assert.Equal("Suppliers.Supplier.CodeRequired", r.Error.Code);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task Update_ChangesFields_AndUnknownSupplierFails()
    {
        await using var db = await NewDb();
        var id = await Create(db);

        var ok = await db.InScopeAsync(sp => sp.GetRequiredService<UpdateSupplierCommandHandler>().HandleAsync(new UpdateSupplierCommand(id, "Renamed", null, "999", "note")));
        var missing = await db.InScopeAsync(sp => sp.GetRequiredService<UpdateSupplierCommandHandler>().HandleAsync(new UpdateSupplierCommand(Guid.NewGuid(), "x", null, null, null)));
        var dto = await Q(db, sp => sp.GetRequiredService<GetSupplierByIdQueryHandler>().HandleAsync(new GetSupplierByIdQuery(id)));

        Assert.True(ok.IsSuccess);
        Assert.Equal("Suppliers.UpdateSupplier.SupplierNotFound", missing.Error.Code);
        Assert.Equal("Renamed", dto!.Name);
        Assert.Null(dto.Email);
        Assert.Equal("999", dto.Phone);
    }

    [Fact]
    public async Task Deactivate_Reactivate_Roundtrip_AndInactiveSuppliersCannotBeEdited()
    {
        await using var db = await NewDb();
        var id = await Create(db);

        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateSupplierCommandHandler>().HandleAsync(new DeactivateSupplierCommand(id)))).IsSuccess);
        var edit = await db.InScopeAsync(sp => sp.GetRequiredService<UpdateSupplierCommandHandler>().HandleAsync(new UpdateSupplierCommand(id, "x", null, null, null)));
        var twice = await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateSupplierCommandHandler>().HandleAsync(new DeactivateSupplierCommand(id)));
        Assert.Equal("Suppliers.Supplier.Inactive", edit.Error.Code);
        Assert.Equal("Suppliers.Supplier.AlreadyInactive", twice.Error.Code);
        Assert.Equal(SupplierStatus.Inactive, (await Q(db, sp => sp.GetRequiredService<GetSupplierByIdQueryHandler>().HandleAsync(new GetSupplierByIdQuery(id))))!.Status);

        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<ReactivateSupplierCommandHandler>().HandleAsync(new ReactivateSupplierCommand(id)))).IsSuccess);
        Assert.Equal(SupplierStatus.Active, (await Q(db, sp => sp.GetRequiredService<GetSupplierByIdQueryHandler>().HandleAsync(new GetSupplierByIdQuery(id))))!.Status);
    }

    [Fact]
    public async Task Deactivate_UnknownSupplier_Fails()
    {
        await using var db = await NewDb();

        var r = await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateSupplierCommandHandler>().HandleAsync(new DeactivateSupplierCommand(Guid.NewGuid())));

        Assert.Equal("Suppliers.DeactivateSupplier.SupplierNotFound", r.Error.Code);
    }

    [Fact]
    public async Task AddressesAndContacts_ArePersisted_AndRemovable()
    {
        await using var db = await NewDb();
        var id = await Create(db);

        var addressId = (await db.InScopeAsync(sp => sp.GetRequiredService<AddSupplierAddressCommandHandler>()
            .HandleAsync(new AddSupplierAddressCommand(id, AddressType.Billing, "1 Main", null, "Town", null, "12345", "USA")))).Value;
        var contactId = (await db.InScopeAsync(sp => sp.GetRequiredService<AddSupplierContactCommandHandler>()
            .HandleAsync(new AddSupplierContactCommand(id, "Jane", "j@x.test", null, "Buyer")))).Value;

        var dto = (await Q(db, sp => sp.GetRequiredService<GetSupplierByIdQueryHandler>().HandleAsync(new GetSupplierByIdQuery(id))))!;
        Assert.Equal(addressId, Assert.Single(dto.Addresses).AddressId);
        Assert.Equal(contactId, Assert.Single(dto.Contacts).ContactId);

        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<RemoveSupplierAddressCommandHandler>().HandleAsync(new RemoveSupplierAddressCommand(id, addressId)))).IsSuccess);
        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<RemoveSupplierContactCommandHandler>().HandleAsync(new RemoveSupplierContactCommand(id, contactId)))).IsSuccess);
        dto = (await Q(db, sp => sp.GetRequiredService<GetSupplierByIdQueryHandler>().HandleAsync(new GetSupplierByIdQuery(id))))!;
        Assert.Empty(dto.Addresses);
        Assert.Empty(dto.Contacts);
    }

    [Fact]
    public async Task AddAddress_ToUnknownSupplier_OrWithBadData_Fails()
    {
        await using var db = await NewDb();
        var id = await Create(db);

        var missing = await db.InScopeAsync(sp => sp.GetRequiredService<AddSupplierAddressCommandHandler>()
            .HandleAsync(new AddSupplierAddressCommand(Guid.NewGuid(), AddressType.Billing, "1", null, "T", null, null, "X")));
        var bad = await db.InScopeAsync(sp => sp.GetRequiredService<AddSupplierAddressCommandHandler>()
            .HandleAsync(new AddSupplierAddressCommand(id, AddressType.Billing, "", null, "T", null, null, "X")));

        Assert.Equal("Suppliers.AddAddress.SupplierNotFound", missing.Error.Code);
        Assert.Equal("Suppliers.Address.Line1Required", bad.Error.Code);
    }

    [Fact]
    public async Task List_IsPaged_Ordered_AndFilterable()
    {
        await using var db = await NewDb();
        foreach (var (code, name) in new[] { ("C3", "Charlie"), ("C1", "Alice"), ("C2", "Bob"), ("C4", "Dave") })
            await Create(db, code, name);
        var bob = (await Q(db, sp => sp.GetRequiredService<SearchSuppliersQueryHandler>().HandleAsync(new SearchSuppliersQuery("Bob")))).Single();
        await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateSupplierCommandHandler>().HandleAsync(new DeactivateSupplierCommand(bob.SupplierId)));

        var first = await Q(db, sp => sp.GetRequiredService<ListSuppliersQueryHandler>().HandleAsync(new ListSuppliersQuery(0, 2)));
        var second = await Q(db, sp => sp.GetRequiredService<ListSuppliersQueryHandler>().HandleAsync(new ListSuppliersQuery(2, 2)));
        var inactive = await Q(db, sp => sp.GetRequiredService<ListSuppliersQueryHandler>().HandleAsync(new ListSuppliersQuery(Status: SupplierStatus.Inactive)));

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

        var page = await Q(db, sp => sp.GetRequiredService<ListSuppliersQueryHandler>().HandleAsync(new ListSuppliersQuery(-5, 100000)));

        Assert.Equal(0, page.Skip);
        Assert.Equal(ListSuppliersQueryHandler.MaxPageSize, page.Take);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Search_MatchesCodeNameEmailPhone_CaseInsensitively_AndEscapesWildcards()
    {
        await using var db = await NewDb();
        await Create(db, "ACME-1", "Acme Corporation", "sales@acme.test");
        await Create(db, "OTHER-2", "Other 100% Co", "x@other.test");

        Task<IReadOnlyList<Suppliers.Application.DTOs.SupplierListItemDto>> Search(string t)
            => Q(db, sp => sp.GetRequiredService<SearchSuppliersQueryHandler>().HandleAsync(new SearchSuppliersQuery(t)));

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

        var byId = await Q(db, sp => sp.GetRequiredService<ISupplierLookup>().FindByIdAsync(id));
        var byCode = await Q(db, sp => sp.GetRequiredService<ISupplierLookup>().FindByCodeAsync("K-1"));
        var none = await Q(db, sp => sp.GetRequiredService<ISupplierLookup>().FindByIdAsync(Guid.NewGuid()));
        var blank = await Q(db, sp => sp.GetRequiredService<ISupplierLookup>().FindByCodeAsync(" "));
        await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateSupplierCommandHandler>().HandleAsync(new DeactivateSupplierCommand(id)));
        var inactive = await Q(db, sp => sp.GetRequiredService<ISupplierLookup>().FindByIdAsync(id));

        Assert.Equal("Contract Co", byId!.Name);
        Assert.Equal(id, byCode!.SupplierId);
        Assert.Null(none);
        Assert.Null(blank);
        Assert.Equal(SupplierStatusContract.Active, byId.Status);
        Assert.Equal(SupplierStatusContract.Inactive, inactive!.Status);
    }

    [Fact]
    public async Task Contracts_Reader_SearchesAndSummarises()
    {
        await using var db = await NewDb();
        var a = await Create(db, "A", "Alpha");
        await Create(db, "B", "Beta");
        await db.InScopeAsync(sp => sp.GetRequiredService<DeactivateSupplierCommandHandler>().HandleAsync(new DeactivateSupplierCommand(a)));

        var found = await Q(db, sp => sp.GetRequiredService<ISupplierReader>().SearchAsync("bet"));
        var summary = await Q(db, sp => sp.GetRequiredService<ISupplierReader>().GetSummaryAsync());

        Assert.Equal("Beta", Assert.Single(found).Name);
        Assert.Equal(new SupplierSummaryResult(2, 1, 1), summary);
        Assert.Empty(await Q(db, sp => sp.GetRequiredService<ISupplierReader>().SearchAsync("")));
    }

    [Fact]
    public async Task Contracts_DoNotExposeDomainTypes()
    {
        var assembly = typeof(ISupplierLookup).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Suppliers.Domain", StringComparison.Ordinal));
        foreach (var method in typeof(ISupplierLookup).GetMethods().Concat(typeof(ISupplierReader).GetMethods()))
            Assert.DoesNotContain("Suppliers.Domain", method.ReturnType.FullName ?? string.Empty);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Data_IsPersisted_AcrossScopes_ThroughTheUnitOfWork()
    {
        await using var db = await NewDb();
        var id = await Create(db);
        await db.InScopeAsync(sp => sp.GetRequiredService<AddSupplierContactCommandHandler>().HandleAsync(new AddSupplierContactCommand(id, "Jane", null, null, null)));

        var contacts = await db.InScopeAsync(sp => sp.GetRequiredService<SuppliersDbContext>().SupplierContacts.CountAsync());

        Assert.Equal(1, contacts);
    }
}
