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
    private readonly ShellViewModel _shell;

    public ShellViewModelTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPermissionProvider>(_ => _permissions);
        var runner = new UiActionRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);

        var catalog = new CapabilityCatalog(
        [
            new CapabilityProvider(
                new CapabilityDescriptor("pos.sale.create", "pos", "Sell", "Sell"),
                new CapabilityDescriptor("catalog.product.create", "catalog", "Create products", "Create products"))
        ]);
        var navigation = new NavigationBuilder(
        [
            new ScreenProvider(
                Screens.Of("pos.sell", "pos", ScreenGroups.Sales, "pos.sale.create"),
                Screens.Of("catalog.products", "catalog", ScreenGroups.Inventory, "catalog.product.create"))
        ], catalog, _licensing);

        _shell = new ShellViewModel(runner, navigation, _user, _factory);
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

        _shell.Reset();
        Assert.Empty(_shell.Groups);
        Assert.Null(_shell.CurrentView);
        Assert.Equal(string.Empty, _shell.SignedInText);

        await _shell.RefreshAsync();
        await _shell.OpenAsync(Entry("pos.sell"));
        Assert.Equal(["pos.sell", "pos.sell"], _factory.Created);
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
}
