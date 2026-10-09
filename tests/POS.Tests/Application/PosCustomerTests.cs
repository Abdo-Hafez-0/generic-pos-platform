using Customers.Contracts.Interfaces;
using Customers.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;

namespace POS.Tests.Application;

/// <summary>
/// FIX-11: an optional customer on a sale. Decisions (user, 2026-10-09): anyone who may sell finds and attaches an existing ACTIVE customer;
/// the till sees code and name only; customers are not created at the till; Customers is optional for POS.
/// </summary>
public sealed class PosCustomerTests
{
    private sealed class StubCustomers : ICustomerLookup, ICustomerReader
    {
        public List<CustomerLookupResult> All { get; } = [];

        public Guid Add(string code, string name, CustomerStatusContract status = CustomerStatusContract.Active, string? phone = null)
        {
            var id = Guid.NewGuid();
            All.Add(new CustomerLookupResult(id, code, name, $"{code.ToLowerInvariant()}@example.test", phone, status));
            return id;
        }

        public Task<CustomerLookupResult?> FindByIdAsync(Guid customerId, CancellationToken cancellationToken = default)
            => Task.FromResult(All.FirstOrDefault(c => c.CustomerId == customerId));

        public Task<CustomerLookupResult?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
            => Task.FromResult(All.FirstOrDefault(c => c.Code == code));

        public Task<IReadOnlyList<CustomerLookupResult>> SearchAsync(string text, int limit = 25, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CustomerLookupResult>>(All.Where(c =>
                c.Code.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || (c.Phone ?? "").Contains(text)).Take(limit).ToList());

        public Task<CustomerSummaryResult> GetSummaryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomerSummaryResult(All.Count, 0, 0));
    }

    private static async Task<(PosTestDatabase Db, Guid Cart, StubCustomers Customers)> TillAsync(bool withCustomers = true, params string[]? capabilities)
    {
        var customers = new StubCustomers();
        var db = await PosTestDatabase.CreateAsync(configureServices: s =>
        {
            if (withCustomers)
            {
                s.AddSingleton<ICustomerLookup>(customers);
                s.AddSingleton<ICustomerReader>(customers);
            }
            if (capabilities is { Length: > 0 })
                s.AddSingleton<IAuthorizationService>(new global::Tests.Common.Security.ScriptedAuthorizationService(capabilities));
        });
        var water = db.Catalog.Register("WATER-1", "Water", 2m);
        db.Inventory.SetStock(water, 10m);
        using var scope = db.CreateScope();
        var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await pos.OpenSessionAsync("cashier", Guid.NewGuid());
        var cart = await pos.StartCartAsync(session.SessionId);
        Assert.True((await pos.AddProductAsync(cart.CartId, "WATER-1", 2m)).IsSuccess);
        return (db, cart.CartId, customers);
    }

    private static async Task<T> Pos<T>(PosTestDatabase db, Func<IPOSService, IPOSReader, Task<T>> call)
    {
        using var scope = db.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<IPOSService>(), scope.ServiceProvider.GetRequiredService<IPOSReader>());
    }

    [Fact]
    public async Task A_cashier_who_may_sell_finds_active_customers_by_code_name_or_phone_and_sees_only_code_and_name()
    {
        // a cashier: may sell, may NOT view customer records (customers.customer.view)
        var (db, _, customers) = await TillAsync(true, POS.Application.Security.POSCapabilities.ManageSession, POS.Application.Security.POSCapabilities.CreateSale);
        await using var _db = db;
        customers.Add("C-001", "Jane Doe", phone: "0100 555 123");
        customers.Add("C-002", "Janet Old", CustomerStatusContract.Inactive);
        customers.Add("C-003", "Adam Jansen");

        var byName = await Pos(db, (pos, _) => pos.FindCustomersAsync("jan"));
        var byPhone = await Pos(db, (pos, _) => pos.FindCustomersAsync("555"));

        Assert.True(byName.IsSuccess, byName.ErrorMessage);
        Assert.Equal(["Adam Jansen", "Jane Doe"], byName.Customers.Select(c => c.Name).ToArray());   // the inactive one is not offered
        Assert.Equal("C-001", Assert.Single(byPhone.Customers).Code);
        Assert.Equal(["CustomerId", "Code", "Name"], typeof(POSCustomerResult).GetProperties().Select(p => p.Name).Except(["EqualityContract"]).ToArray());
    }

    [Fact]
    public async Task The_customer_is_kept_with_the_open_cart_and_reaches_the_sale_as_a_snapshot()
    {
        var (db, cart, customers) = await TillAsync();
        await using var _db = db;
        var jane = customers.Add("C-001", "Jane Doe");

        Assert.True((await Pos(db, (pos, _) => pos.SetCustomerAsync(cart, jane))).IsSuccess);
        var stored = await Pos(db, (_, reader) => reader.GetCartAsync(cart));   // a new scope: read back from the database (a resumed till)
        Assert.Equal((jane, "C-001", "Jane Doe"), (stored!.CustomerId, stored.CustomerCode, stored.CustomerName));

        var sold = await Pos(db, (pos, _) => pos.CheckoutAsync(cart));
        Assert.True(sold.IsSuccess, sold.ErrorMessage);
        Assert.Equal(new Sales.Contracts.Models.SaleCustomer(jane, "C-001", "Jane Doe"), db.Sales.CreatedCustomer);
    }

    [Fact]
    public async Task Removing_the_customer_sells_without_one()
    {
        var (db, cart, customers) = await TillAsync();
        await using var _db = db;
        await Pos(db, (pos, _) => pos.SetCustomerAsync(cart, customers.Add("C-001", "Jane Doe")));

        Assert.True((await Pos(db, (pos, _) => pos.SetCustomerAsync(cart, null))).IsSuccess);
        Assert.Null((await Pos(db, (_, reader) => reader.GetCartAsync(cart)))!.CustomerId);
        Assert.True((await Pos(db, (pos, _) => pos.CheckoutAsync(cart))).IsSuccess);
        Assert.Null(db.Sales.CreatedCustomer);
    }

    [Fact]
    public async Task Unknown_or_inactive_customers_are_refused_and_the_cart_is_unchanged()
    {
        var (db, cart, customers) = await TillAsync();
        await using var _db = db;
        var gone = customers.Add("C-009", "Old Customer", CustomerStatusContract.Inactive);

        var inactive = await Pos(db, (pos, _) => pos.SetCustomerAsync(cart, gone));
        var unknown = await Pos(db, (pos, _) => pos.SetCustomerAsync(cart, Guid.NewGuid()));
        var tooShort = await Pos(db, (pos, _) => pos.FindCustomersAsync("j"));

        Assert.Equal("POS.Customer.Inactive", inactive.ErrorCode);
        Assert.Equal("POS.Customer.NotFound", unknown.ErrorCode);
        Assert.Equal("POS.Customer.SearchTooShort", tooShort.ErrorCode);
        Assert.Null((await Pos(db, (_, reader) => reader.GetCartAsync(cart)))!.CustomerId);
    }

    [Fact]
    public async Task Without_the_Customers_module_the_till_says_so_and_still_sells()
    {
        var (db, cart, _) = await TillAsync(withCustomers: false);
        await using var _db = db;

        var found = await Pos(db, (pos, _) => pos.FindCustomersAsync("jane"));
        var set = await Pos(db, (pos, _) => pos.SetCustomerAsync(cart, Guid.NewGuid()));

        Assert.Equal("POS.Customer.Unavailable", found.ErrorCode);
        Assert.Equal("POS.Customer.Unavailable", set.ErrorCode);
        Assert.True((await Pos(db, (pos, _) => pos.CheckoutAsync(cart))).IsSuccess);
    }

    [Fact]
    public async Task Someone_who_may_not_sell_cannot_search_customers_or_choose_one()
    {
        var (db, cart, customers) = await TillAsync(true, POS.Application.Security.POSCapabilities.ManageSession, POS.Application.Security.POSCapabilities.CreateSale);
        await using var _db = db;
        var jane = customers.Add("C-001", "Jane Doe");
        using (var scope = db.CreateScope())
            ((global::Tests.Common.Security.ScriptedAuthorizationService)scope.ServiceProvider.GetRequiredService<IAuthorizationService>()).Allowed.Remove(POS.Application.Security.POSCapabilities.CreateSale);

        Assert.False((await Pos(db, (pos, _) => pos.FindCustomersAsync("jane"))).IsSuccess);
        Assert.False((await Pos(db, (pos, _) => pos.SetCustomerAsync(cart, jane))).IsSuccess);
        Assert.Null((await Pos(db, (_, reader) => reader.GetCartAsync(cart)))!.CustomerId);
    }
}
