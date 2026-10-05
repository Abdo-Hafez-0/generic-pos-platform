using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;
using POS.Application.Commands;
using POS.Application.Devices;
using POS.Application.Security;
using POS.Contracts.Interfaces;
using Tests.Common.Hardware;
using Tests.Common.Security;

namespace POS.Tests.Security;

/// <summary>
/// POS operations are protected where they are EXECUTED: the handlers refuse before doing anything, so it does not matter whether the call
/// comes through IPOSService, IPOSDevices or a handler resolved straight from the container (what a script or a hostile screen would do).
/// </summary>
public sealed class PosAuthorizationTests
{
    private sealed class World : IAsyncDisposable
    {
        public required PosTestDatabase Db { get; init; }
        public required ScriptedAuthorizationService Auth { get; init; }
        public required FakeReceiptPrinter Printer { get; init; }
        public required FakeCashDrawer Drawer { get; init; }
        public required FakeLabelPrinter Label { get; init; }
        public required FakeScale Scale { get; init; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<World> StartAsync(params string[] allowed)
    {
        var auth = new ScriptedAuthorizationService(allowed);
        var printer = new FakeReceiptPrinter();
        var drawer = new FakeCashDrawer();
        var label = new FakeLabelPrinter();
        var scale = new FakeScale();
        var db = await PosTestDatabase.CreateAsync(configureServices: s =>
        {
            s.AddSingleton<IAuthorizationService>(auth);
            s.AddSingleton<IReceiptPrinter>(printer);
            s.AddSingleton<ICashDrawer>(drawer);
            s.AddSingleton<ILabelPrinter>(label);
            s.AddSingleton<IScale>(scale);
        });
        return new World { Db = db, Auth = auth, Printer = printer, Drawer = drawer, Label = label, Scale = scale };
    }

    /// <summary>A cart with one item, prepared with every capability (then the test narrows what is allowed).</summary>
    private static async Task<(Guid Cart, Guid Session)> PrepareCartAsync(World w)
    {
        var product = w.Db.Catalog.Register("SKU-1", "Cola", 10m);
        w.Db.Inventory.SetStock(product, 100m);
        var before = w.Auth.Allowed.ToArray();
        foreach (var c in POSCapabilities.All) w.Auth.Allowed.Add(c.Code);

        using var scope = w.Db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await service.OpenSessionAsync("cashier-7", Guid.NewGuid());
        var cart = await service.StartCartAsync(session.SessionId);
        Assert.True((await service.AddProductAsync(cart.CartId, "SKU-1", 2m)).IsSuccess);

        w.Auth.Allowed.Clear();
        foreach (var c in before) w.Auth.Allowed.Add(c);
        w.Auth.Asked.Clear();
        return (cart.CartId, session.SessionId);
    }

    // ------------------------------------------------------------------ the service API

    [Fact]
    public async Task Opening_a_session_needs_pos_session_manage_and_stores_nothing_without_it()
    {
        await using var w = await StartAsync();
        using var scope = w.Db.CreateScope();

        var denied = await scope.ServiceProvider.GetRequiredService<IPOSService>().OpenSessionAsync("cashier", Guid.NewGuid());

        Assert.False(denied.IsSuccess);
        Assert.Equal(SecurityErrors.ForbiddenCode, denied.ErrorCode);
        Assert.Contains(POSCapabilities.ManageSession, w.Auth.Asked);

        w.Auth.Allowed.Add(POSCapabilities.ManageSession);
        Assert.True((await scope.ServiceProvider.GetRequiredService<IPOSService>().OpenSessionAsync("cashier", Guid.NewGuid())).IsSuccess);
    }

    [Fact]
    public async Task Cart_editing_needs_pos_sale_create()
    {
        await using var w = await StartAsync();
        var (cart, session) = await PrepareCartAsync(w);
        using var scope = w.Db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var product = Guid.NewGuid();

        var results = new (string Name, string? Code)[]
        {
            ("start", (await service.StartCartAsync(session)).ErrorCode),
            ("add", (await service.AddProductAsync(cart, "SKU-1", 1m)).ErrorCode),
            ("remove", (await service.RemoveProductAsync(cart, product)).ErrorCode),
            ("quantity", (await service.ChangeQuantityAsync(cart, product, 3m)).ErrorCode),
            ("clear", (await service.ClearCartAsync(cart)).ErrorCode)
        };

        Assert.All(results, r => Assert.True(r.Code == SecurityErrors.ForbiddenCode, $"{r.Name}: {r.Code}"));
        w.Auth.Allowed.Add(POSCapabilities.CreateSale);
        Assert.True((await service.AddProductAsync(cart, "SKU-1", 1m)).IsSuccess);
    }

    [Fact]
    public async Task A_refused_checkout_changes_nothing_anywhere()
    {
        await using var w = await StartAsync();
        var (cart, _) = await PrepareCartAsync(w);
        using var scope = w.Db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cart);

        Assert.False(result.IsSuccess);
        Assert.Equal(SecurityErrors.ForbiddenCode, result.ErrorCode);
        Assert.Empty(w.Db.Sales.Calls);               // no sale was created
        Assert.Empty(w.Db.Inventory.Issued);          // no stock moved
        Assert.Empty(w.Printer.Printed);              // no receipt
        Assert.Equal(0, w.Drawer.Attempts);           // no drawer kick
        var read = await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetCartAsync(cart);
        Assert.Equal("Open", read!.Status.ToString());
    }

    [Fact]
    public async Task An_authorized_checkout_completes_as_before()
    {
        await using var w = await StartAsync(POSCapabilities.CreateSale);
        var (cart, _) = await PrepareCartAsync(w);
        using var scope = w.Db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cart);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Single(w.Printer.Printed);
        Assert.Contains(POSCapabilities.CreateSale, w.Auth.Asked);
    }

    // ------------------------------------------------------------------ devices

    [Fact]
    public async Task Opening_the_drawer_without_a_sale_needs_its_own_capability()
    {
        await using var w = await StartAsync(POSCapabilities.CreateSale, POSCapabilities.ManageSession); // selling is not enough
        using var scope = w.Db.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IPOSDevices>();

        var denied = await devices.OpenCashDrawerAsync();

        Assert.False(denied.IsSuccess);
        Assert.Equal(SecurityErrors.ForbiddenCode, denied.ErrorCode);
        Assert.Equal(0, w.Drawer.Attempts);

        w.Auth.Allowed.Add(POSCapabilities.OpenDrawer);
        Assert.True((await devices.OpenCashDrawerAsync()).IsSuccess);
        Assert.Equal(1, w.Drawer.Opened);
    }

    [Fact]
    public async Task Reprinting_labels_and_reading_the_scale_each_need_their_capability()
    {
        await using var w = await StartAsync();
        var (cart, _) = await PrepareCartAsync(w);
        using var scope = w.Db.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IPOSDevices>();

        Assert.Equal(SecurityErrors.ForbiddenCode, (await devices.PrintReceiptAsync(cart)).ErrorCode);
        Assert.Equal(SecurityErrors.ForbiddenCode, (await devices.PrintProductLabelAsync("SKU-1")).ErrorCode);
        Assert.Equal(SecurityErrors.ForbiddenCode, (await devices.ReadWeightAsync()).ErrorCode);
        Assert.Equal(0, w.Printer.Attempts);
        Assert.Equal(0, w.Label.Attempts);

        w.Auth.Allowed.Add(POSCapabilities.PrintLabel);
        Assert.True((await devices.PrintProductLabelAsync("SKU-1")).IsSuccess);
        Assert.Equal(1, w.Label.Printed.Count);
        w.Auth.Allowed.Add(POSCapabilities.CreateSale);
        Assert.True((await devices.ReadWeightAsync()).IsSuccess);
    }

    [Fact]
    public async Task Device_status_stays_readable_so_a_signed_out_till_can_still_show_what_is_wrong()
    {
        await using var w = await StartAsync();
        using var scope = w.Db.CreateScope();

        var status = await scope.ServiceProvider.GetRequiredService<IPOSDevices>().GetDeviceStatusAsync();

        Assert.NotEmpty(status);
        Assert.Empty(w.Auth.Asked);
    }

    // ------------------------------------------------------------------ bypassing the service layer

    [Fact]
    public async Task Handlers_resolved_directly_are_protected_too()
    {
        await using var w = await StartAsync();
        var (cart, session) = await PrepareCartAsync(w);
        using var scope = w.Db.CreateScope();
        var sp = scope.ServiceProvider;

        var results = new Result[]
        {
            await sp.GetRequiredService<OpenPosSessionCommandHandler>().HandleAsync(new OpenPosSessionCommand("x", Guid.NewGuid())),
            await sp.GetRequiredService<ClosePosSessionCommandHandler>().HandleAsync(new ClosePosSessionCommand(session)),
            await sp.GetRequiredService<StartCartCommandHandler>().HandleAsync(new StartCartCommand(session)),
            await sp.GetRequiredService<AddProductToCartCommandHandler>().HandleAsync(new AddProductToCartCommand(cart, "SKU-1", 1m)),
            await sp.GetRequiredService<CheckoutCartCommandHandler>().HandleAsync(new CheckoutCartCommand(cart)),
            await sp.GetRequiredService<OpenCashDrawerCommandHandler>().HandleAsync(),
            await sp.GetRequiredService<PrintReceiptCommandHandler>().HandleAsync(new PrintReceiptCommand(cart)),
            await sp.GetRequiredService<PrintProductLabelCommandHandler>().HandleAsync(new PrintProductLabelCommand("SKU-1")),
            await sp.GetRequiredService<ReadWeightQueryHandler>().HandleAsync()
        };

        Assert.All(results, r => Assert.Equal(SecurityErrors.ForbiddenCode, r.Error.Code));
        Assert.Empty(w.Db.Sales.Calls);
    }

    [Fact]
    public async Task With_the_real_authorization_service_nobody_signed_in_means_no_sale()
    {
        var session = new SessionContext();
        var permissions = new FixedPermissions();
        var catalog = new CapabilityCatalog([new POSCapabilityProvider()]);
        await using var db = await PosTestDatabase.CreateAsync(configureServices: s =>
            s.AddSingleton<IAuthorizationService>(new AuthorizationService(session, catalog, permissions)));
        var product = db.Catalog.Register("SKU-1", "Cola", 10m);
        db.Inventory.SetStock(product, 10m);
        using var scope = db.CreateScope();

        var open = await scope.ServiceProvider.GetRequiredService<IPOSService>().OpenSessionAsync("cashier", Guid.NewGuid());
        Assert.Equal(SecurityErrors.NotAuthenticatedCode, open.ErrorCode);

        var cashier = Guid.NewGuid();
        permissions.Held[cashier] = [POSCapabilities.ManageSession, POSCapabilities.CreateSale];
        session.SignIn(new AuthenticatedIdentity(cashier, "cashier", "Cashier"));
        var opened = await scope.ServiceProvider.GetRequiredService<IPOSService>().OpenSessionAsync("cashier", Guid.NewGuid());
        Assert.True(opened.IsSuccess);

        permissions.Held[cashier].Remove(POSCapabilities.CreateSale);   // a role change takes effect on the very next call
        var cart = await scope.ServiceProvider.GetRequiredService<IPOSService>().StartCartAsync(opened.SessionId);
        Assert.Equal(SecurityErrors.ForbiddenCode, cart.ErrorCode);
    }

    // ------------------------------------------------------------------ the declaration itself

    [Fact]
    public void Every_capability_is_a_valid_unique_code_owned_by_the_pos_module()
    {
        var catalog = new CapabilityCatalog([new POSCapabilityProvider()]);

        Assert.Equal(POSCapabilities.All.Count, catalog.All.Count);
        Assert.All(catalog.All, c => Assert.Equal("pos", c.Module));
        Assert.True(catalog.Find(POSCapabilities.OpenDrawer)!.IsSensitive);
    }

    internal sealed class FixedPermissions : IPermissionProvider
    {
        public Dictionary<Guid, HashSet<string>> Held { get; } = [];

        public Task<IReadOnlyCollection<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>(Held.TryGetValue(userId, out var set) ? set.ToList() : []);
    }
}
