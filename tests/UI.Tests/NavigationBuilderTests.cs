using Platform.Application.Abstractions.Authorization;
using Platform.Core.Licensing;
using Platform.Presentation.Screens;

namespace UI.Tests;

/// <summary>FIX-01 decision 2: no permission = hidden; permission without license = shown locked with a reason; fail closed.</summary>
public sealed class NavigationBuilderTests
{
    private static readonly CapabilityCatalog Catalog = new(
    [
        new CapabilityProvider(
            new CapabilityDescriptor("pos.sale.create", "pos", "Sell", "Sell"),
            new CapabilityDescriptor("users.view", "users", "View users", "View users", LicenseRequirement.None),
            new CapabilityDescriptor("catalog.product.create", "catalog", "Create products", "Create products"))
    ]);

    private static NavigationBuilder Builder(FakeLicensing? licensing, params ScreenDescriptor[] screens)
        => new([new ScreenProvider(screens)], Catalog, licensing);

    [Fact]
    public void A_screen_whose_capability_the_user_does_not_hold_is_hidden()
    {
        var builder = Builder(new FakeLicensing("pos"), Screens.Of("pos.sell", "pos", ScreenGroups.Sales, "pos.sale.create"));

        Assert.Empty(builder.Build([]));
    }

    [Fact]
    public void A_screen_whose_capability_the_user_holds_is_available_and_permission_codes_compare_ignoring_case()
    {
        var builder = Builder(new FakeLicensing("pos"), Screens.Of("pos.sell", "pos", ScreenGroups.Sales, "pos.sale.create"));

        var entry = Assert.Single(Assert.Single(builder.Build(["POS.Sale.Create"])).Entries);
        Assert.True(entry.IsAvailable);
        Assert.Null(entry.Reason);
        Assert.Equal("Title of pos.sell", entry.Title);
    }

    [Fact]
    public void A_licensed_capability_of_an_unlicensed_module_is_shown_locked_with_a_plain_reason()
    {
        var licensing = new FakeLicensing("catalog") { State = LicenseState.Expired };
        var builder = Builder(licensing, Screens.Of("pos.sell", "pos", ScreenGroups.Sales, "pos.sale.create"));

        var entry = Assert.Single(Assert.Single(builder.Build(["pos.sale.create"])).Entries);
        Assert.Equal(ScreenAvailability.LicenseLocked, entry.Availability);
        Assert.False(entry.IsAvailable);
        Assert.Contains("Expired", entry.Reason);
        Assert.Contains("data is safe", entry.Reason);
    }

    [Fact]
    public void A_capability_that_needs_no_license_stays_available_without_a_license()
    {
        var builder = Builder(new FakeLicensing { State = LicenseState.Unlicensed }, Screens.Of("users.list", "users", ScreenGroups.Administration, "users.view"));

        Assert.True(Assert.Single(Assert.Single(builder.Build(["users.view"])).Entries).IsAvailable);
    }

    [Fact]
    public void A_screen_without_a_capability_is_available_to_every_signed_in_user()
    {
        var builder = Builder(new FakeLicensing(), Screens.Of("reports.home", "reporting", ScreenGroups.Reports));

        Assert.True(Assert.Single(Assert.Single(builder.Build([])).Entries).IsAvailable);
    }

    [Fact]
    public void A_screen_naming_a_capability_nobody_declared_is_hidden_even_if_the_user_holds_that_code()
    {
        var builder = Builder(new FakeLicensing("pos"), Screens.Of("pos.ghost", "pos", ScreenGroups.Sales, "pos.ghost.open"));

        Assert.Empty(builder.Build(["pos.ghost.open"]));
    }

    [Fact]
    public void Without_a_licensing_component_nothing_is_locked()
    {
        var builder = Builder(null, Screens.Of("pos.sell", "pos", ScreenGroups.Sales, "pos.sale.create"));

        Assert.True(Assert.Single(Assert.Single(builder.Build(["pos.sale.create"])).Entries).IsAvailable);
    }

    [Fact]
    public void Groups_follow_the_fixed_order_entries_follow_their_order_and_empty_groups_are_left_out()
    {
        var builder = Builder(new FakeLicensing("pos", "catalog"),
            Screens.Of("users.list", "users", ScreenGroups.Administration, "users.view"),
            Screens.Of("pos.history", "pos", ScreenGroups.Sales, order: 2),
            Screens.Of("pos.sell", "pos", ScreenGroups.Sales, order: 1),
            Screens.Of("catalog.products", "catalog", ScreenGroups.Inventory, "catalog.product.create"));

        var groups = builder.Build(["users.view"]);

        Assert.Equal([ScreenGroups.Sales, ScreenGroups.Administration], groups.Select(g => g.Id));
        Assert.Equal(["pos.sell", "pos.history"], groups[0].Entries.Select(e => e.Screen.Id));
        Assert.Equal(ScreenGroups.TitleOf(ScreenGroups.Sales), groups[0].Title);
    }

    [Fact]
    public void A_screen_declared_twice_is_a_programming_error()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Builder(null,
            Screens.Of("pos.sell", "pos", ScreenGroups.Sales),
            Screens.Of("pos.sell", "pos", ScreenGroups.Reports)));

        Assert.Contains("more than once", error.Message);
    }

    [Theory]
    [InlineData("Pos Sell", "pos", ScreenGroups.Sales, null)]
    [InlineData("pos.sell", " ", ScreenGroups.Sales, null)]
    [InlineData("pos.sell", "pos", "elsewhere", null)]
    [InlineData("pos.sell", "pos", ScreenGroups.Sales, "Not A Capability")]
    public void A_malformed_screen_declaration_is_refused(string id, string module, string group, string? capability)
        => Assert.Throws<InvalidOperationException>(() => Builder(null, Screens.Of(id, module, group, capability)));

    [Fact]
    public void A_view_without_a_parameterless_constructor_is_refused()
        => Assert.Throws<InvalidOperationException>(() => Builder(null, Screens.Of("pos.sell", "pos", ScreenGroups.Sales, view: typeof(Uri))));

    [Fact]
    public void Every_group_has_a_title()
        => Assert.All(ScreenGroups.All, g => Assert.False(string.IsNullOrWhiteSpace(ScreenGroups.TitleOf(g))));
}
