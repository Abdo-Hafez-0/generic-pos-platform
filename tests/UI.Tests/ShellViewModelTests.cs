using Client.Desktop.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Licensing;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;

namespace UI.Tests;

/// <summary>The shell state (FIX-01a): navigation for the user signed in NOW, screens opened once per session, locked screens explained.</summary>
public sealed class ShellViewModelTests
{
    private readonly FakePermissions _permissions = new();
    private readonly FakeCurrentUser _user = new();
    private readonly FakeScreenFactory _factory = new();
    private readonly FakeLicensing _licensing = new("pos");
    private readonly FakeNotice _notice = new();
    private readonly ShellViewModel _shell;

    /// <summary>MISS-11a-3: a module's status-bar warning for holders of pos.drawer.count.</summary>
    private sealed class FakeNotice : IShellNoticeProvider
    {
        public string? Text { get; set; }
        public int Asked { get; private set; }
        public bool Throws { get; set; }
        public string RequiredCapability => "pos.drawer.count";

        public Task<string?> GetNoticeAsync(CancellationToken cancellationToken = default)
        {
            Asked++;
            if (Throws) throw new InvalidOperationException("broken");
            return Task.FromResult(Text);
        }
    }

    public ShellViewModelTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPermissionProvider>(_ => _permissions);
        var runner = new UiActionRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);

        var catalog = new CapabilityCatalog(
        [
            new CapabilityProvider(
                new CapabilityDescriptor("pos.sale.create", "pos", "Sell", "Sell"),
                new CapabilityDescriptor("pos.drawer.count", "pos", "Count the drawer", "Count the drawer"),
                new CapabilityDescriptor("catalog.product.create", "catalog", "Create products", "Create products"))
        ]);
        var navigation = new NavigationBuilder(
        [
            new ScreenProvider(
                Screens.Of("pos.sell", "pos", ScreenGroups.Sales, "pos.sale.create", handles: [ScreenRequests.ReturnSale]),
                Screens.Of("pos.drawer", "pos", ScreenGroups.Sales, "pos.drawer.count", order: 1),
                Screens.Of("catalog.products", "catalog", ScreenGroups.Inventory, "catalog.product.create"))
        ], catalog, _licensing);

        _shell = new ShellViewModel(runner, navigation, _user, _factory, noticeProviders: [_notice]);
    }

    private NavigationEntry Entry(string id) => _shell.Groups.SelectMany(g => g.Entries).Single(e => e.Screen.Id == id);

    [Fact]
    public async Task The_navigation_is_built_from_the_permissions_of_the_signed_in_user()
    {
        _permissions.Held.Add("pos.sale.create");

        await _shell.RefreshAsync();

        Assert.Equal(_user.UserId, _permissions.AskedFor);
        Assert.Equal("pos.sell", Assert.Single(Assert.Single(_shell.Groups).Entries).Screen.Id);
        Assert.False(_shell.HasNoScreens);
        Assert.Contains(_user.DisplayName, _shell.SignedInText);
        Assert.True(_shell.IsHome);
    }

    [Fact]
    public async Task A_user_without_permissions_sees_no_screen_and_is_told_so()
    {
        await _shell.RefreshAsync();

        Assert.Empty(_shell.Groups);
        Assert.True(_shell.HasNoScreens);
        Assert.Null(_shell.ErrorMessage);
    }

    [Fact]
    public async Task When_the_permissions_cannot_be_read_the_shell_fails_closed_with_a_plain_message()
    {
        _permissions.Held.Add("pos.sale.create");
        await _shell.RefreshAsync();
        _permissions.Failure = new InvalidOperationException("SQLite Error 11: database disk image is malformed");

        await _shell.RefreshAsync();

        Assert.Empty(_shell.Groups);
        Assert.NotNull(_shell.ErrorMessage);
        Assert.DoesNotContain("SQLite", _shell.ErrorMessage);
    }

    [Fact]
    public async Task A_screen_is_created_once_per_session_and_told_each_time_it_is_shown()
    {
        _permissions.Held.Add("pos.sale.create");
        await _shell.RefreshAsync();

        await _shell.OpenAsync(Entry("pos.sell"));
        var view = _shell.CurrentView;
        await _shell.OpenAsync(Entry("pos.sell"));

        Assert.Equal(["pos.sell"], _factory.Created);
        Assert.Same(view, _shell.CurrentView);
        Assert.Equal("Title of pos.sell", _shell.CurrentTitle);
        Assert.False(_shell.IsHome);
    }

    [Fact]
    public async Task Opening_through_the_command_works_like_opening_directly()
    {
        _permissions.Held.Add("pos.sale.create");
        await _shell.RefreshAsync();

        _shell.OpenCommand.Execute(Entry("pos.sell"));

        Assert.Equal(["pos.sell"], _factory.Created);
        Assert.NotNull(_shell.CurrentView);
    }

    [Fact]
    public async Task A_locked_screen_is_not_created_its_reason_is_shown_instead()
    {
        _licensing.State = LicenseState.GracePeriod;
        _permissions.Held.Add("catalog.product.create");
        await _shell.RefreshAsync();

        await _shell.OpenAsync(Entry("catalog.products"));

        Assert.Empty(_factory.Created);
        Assert.Null(_shell.CurrentView);
        Assert.Contains("GracePeriod", _shell.LockedReason);
        Assert.False(_shell.IsHome);
    }

    [Fact]
    public async Task Signing_out_forgets_every_screen_so_the_next_user_starts_clean()
    {
        _permissions.Held.Add("pos.sale.create");
        await _shell.RefreshAsync();
        await _shell.OpenAsync(Entry("pos.sell"));

        await _shell.ResetAsync();
        Assert.Empty(_shell.Groups);
        Assert.Null(_shell.CurrentView);
        Assert.Equal(string.Empty, _shell.SignedInText);

        await _shell.RefreshAsync();
        await _shell.OpenAsync(Entry("pos.sell"));
        Assert.Equal(["pos.sell", "pos.sell"], _factory.Created);
    }

    private FakeViewModel ViewModel(string id) => (FakeViewModel)_factory.ViewModels[id];

    [Fact]
    public async Task Opening_another_screen_tells_the_shown_one_it_was_left()
    {
        _permissions.Held.AddRange(["pos.sale.create", "pos.drawer.count", "catalog.product.create"]);
        await _shell.RefreshAsync();

        await _shell.OpenAsync(Entry("pos.sell"));
        await _shell.OpenAsync(Entry("pos.sell"));      // shown again: not left
        Assert.Equal(0, ViewModel("pos.sell").Leaves);

        await _shell.OpenAsync(Entry("pos.drawer"));
        Assert.Equal(1, ViewModel("pos.sell").Leaves);
        Assert.Equal(0, ViewModel("pos.drawer").Leaves);

        await _shell.OpenAsync(Entry("pos.sell"));
        Assert.Equal(3, ViewModel("pos.sell").Navigations);
        Assert.Equal(1, ViewModel("pos.drawer").Leaves);
    }

    [Fact]
    public async Task Choosing_a_locked_entry_leaves_the_shown_screen()
    {
        _permissions.Held.AddRange(["pos.sale.create", "pos.drawer.count", "catalog.product.create"]);
        await _shell.RefreshAsync();
        await _shell.OpenAsync(Entry("pos.sell"));

        await _shell.OpenAsync(Entry("catalog.products"));      // the catalog module is not licensed here

        Assert.NotNull(_shell.LockedReason);
        Assert.Equal(1, ViewModel("pos.sell").Leaves);
    }

    [Fact]
    public async Task Signing_out_leaves_the_shown_screen_once()
    {
        _permissions.Held.Add("pos.sale.create");
        await _shell.RefreshAsync();
        await _shell.OpenAsync(Entry("pos.sell"));

        await _shell.ResetAsync();
        await _shell.ResetAsync();

        Assert.Equal(1, ViewModel("pos.sell").Leaves);
    }

    [Fact]
    public async Task A_request_opens_the_screen_that_handles_it_and_passes_the_request_after_it_is_shown()
    {
        _permissions.Held.AddRange(["pos.sale.create", "pos.drawer.count"]);
        await _shell.RefreshAsync();
        await _shell.OpenAsync(Entry("pos.drawer"));
        var request = new ScreenRequest(ScreenRequests.ReturnSale, Guid.NewGuid());

        Assert.True(_shell.CanOpenFor(ScreenRequests.ReturnSale));
        Assert.True(await _shell.OpenForAsync(request));

        Assert.Equal("Title of pos.sell", _shell.CurrentTitle);                         // the menu shows the till as the open screen
        Assert.Equal([(request, 1)], ViewModel("pos.sell").Requests);                    // after it loaded
        Assert.Equal(1, ViewModel("pos.drawer").Leaves);
    }

    [Fact]
    public async Task Without_a_screen_the_user_may_open_for_it_a_request_opens_nothing()
    {
        _permissions.Held.Add("pos.drawer.count");                                       // may not sell: the till is hidden
        await _shell.RefreshAsync();

        Assert.False(_shell.CanOpenFor(ScreenRequests.ReturnSale));
        Assert.False(await _shell.OpenForAsync(new ScreenRequest(ScreenRequests.ReturnSale, Guid.NewGuid())));
        Assert.False(_shell.CanOpenFor("unknown.kind"));
        Assert.Empty(_factory.Created);
    }

    [Fact]
    public async Task A_locked_screen_does_not_handle_requests()
    {
        _permissions.Held.Add("catalog.product.create");
        var navigation = new NavigationBuilder([new ScreenProvider(Screens.Of("catalog.products", "catalog", ScreenGroups.Inventory, "catalog.product.create",
            handles: ["product.open"]))], new CapabilityCatalog([new CapabilityProvider(new CapabilityDescriptor("catalog.product.create", "catalog", "Create", "Create"))]),
            new FakeLicensing("pos") { State = LicenseState.GracePeriod });
        var services = new ServiceCollection();
        services.AddScoped<IPermissionProvider>(_ => _permissions);
        var shell = new ShellViewModel(new UiActionRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance),
            navigation, _user, _factory);
        await shell.RefreshAsync();

        Assert.False(shell.CanOpenFor("product.open"));
    }

    [Fact]
    public async Task A_role_change_shows_on_the_next_refresh()
    {
        await _shell.RefreshAsync();
        Assert.Empty(_shell.Groups);

        _permissions.Held.Add("pos.sale.create");
        await _shell.RefreshAsync();

        Assert.Single(_shell.Groups);
    }

    [Fact]
    public async Task A_modules_warning_shows_only_to_those_who_may_act_on_it_and_follows_what_the_module_says()
    {
        _notice.Text = "Electronic receipts: 2 refused by the tax authority";
        _permissions.Held.Add("pos.sale.create");
        await _shell.RefreshAsync();
        Assert.Equal(string.Empty, _shell.ModuleNoticeText);
        Assert.Equal(0, _notice.Asked);                                                  // not even asked for someone who cannot act

        _permissions.Held.Add("pos.drawer.count");
        await _shell.RefreshAsync();
        Assert.Equal("Electronic receipts: 2 refused by the tax authority", _shell.ModuleNoticeText);

        _notice.Text = null;
        await _shell.OpenAsync(Entry("pos.sell"));                                      // opening a screen asks again
        Assert.Equal(string.Empty, _shell.ModuleNoticeText);

        _notice.Throws = true;
        await _shell.UpdateModuleNoticesAsync();                                          // a broken provider never breaks the shell
        Assert.Equal(string.Empty, _shell.ModuleNoticeText);

        _notice.Throws = false;
        _notice.Text = "x";
        await _shell.UpdateModuleNoticesAsync();
        await _shell.ResetAsync();
        Assert.Equal(string.Empty, _shell.ModuleNoticeText);                              // signing out forgets it
    }
}
