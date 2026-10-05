using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Licensing;
using Platform.Core.Licensing;
using Platform.Core.Modules;
using Security.Tests.Foundation;

namespace Security.Tests.Licensing;

/// <summary>The central license gate in AuthorizationService: one place, driven by the capability declaration, never by business code.</summary>
public sealed class LicenseGateTests
{
    private sealed class FakeLicense(LicenseState state, params string[] modules) : ILicenseEntitlementService
    {
        public LicenseState State { get; set; } = state;

        public List<string> Modules { get; } = [.. modules];

        public int Asked { get; private set; }

        public bool IsModuleLicensed(ModuleId moduleId)
        {
            Asked++;
            return State is LicenseState.Active or LicenseState.GracePeriod && Modules.Contains(moduleId.Value);
        }

        public bool IsFeatureLicensed(FeatureId featureId) => false;
    }

    private static readonly CapabilityDescriptor[] Descriptors =
    [
        new("pos.sale.create", "pos", "Sell", "d"),
        new("inventory.stock.adjust", "inventory", "Adjust", "d"),
        new("reporting.view", "reporting", "Reports", "d", LicenseRequirement.None),
        new("users.manage", "users", "Users", "d", LicenseRequirement.None)
    ];

    private static (AuthorizationService Service, FakePermissionProvider Permissions, RecordingSink Events) Build(FakeLicense? license)
    {
        var session = new SessionContext();
        var permissions = new FakePermissionProvider();
        var user = Guid.NewGuid();
        permissions.Held[user] = [.. Descriptors.Select(d => d.Code)]; // the user may do EVERYTHING: only the license can say no
        session.SignIn(new AuthenticatedIdentity(user, "admin", "Admin"));
        var events = new RecordingSink();
        return (new AuthorizationService(session, new CapabilityCatalog([new FakeCapabilityProvider(Descriptors)]), permissions, events, license), permissions, events);
    }

    [Fact]
    public async Task An_active_license_that_includes_the_module_allows_it()
    {
        var (service, _, events) = Build(new FakeLicense(LicenseState.Active, "pos", "inventory"));

        Assert.True((await service.AuthorizeAsync("pos.sale.create")).IsSuccess);
        Assert.True((await service.AuthorizeAsync("inventory.stock.adjust")).IsSuccess);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task A_module_missing_from_the_license_is_restricted_while_the_others_work()
    {
        var (service, _, events) = Build(new FakeLicense(LicenseState.Active, "pos"));

        Assert.True((await service.AuthorizeAsync("pos.sale.create")).IsSuccess);
        var restricted = await service.AuthorizeAsync("inventory.stock.adjust");

        Assert.Equal(SecurityErrors.LicenseRestrictedCode, restricted.Error.Code);
        Assert.Equal("Security.LicenseRestricted", Assert.Single(events.Events).Summary);
    }

    [Theory]
    [InlineData(LicenseState.Unlicensed)]
    [InlineData(LicenseState.Expired)]
    [InlineData(LicenseState.Suspended)]
    [InlineData(LicenseState.Revoked)]
    [InlineData(LicenseState.Invalid)]
    public async Task Without_a_usable_license_licensed_operations_are_refused_but_the_data_stays_reachable(LicenseState state)
    {
        var (service, _, _) = Build(new FakeLicense(state, "pos", "inventory", "reporting", "users"));

        var sale = await service.AuthorizeAsync("pos.sale.create");
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, sale.Error.Code);
        Assert.Contains(state.ToString(), sale.Error.Description);
        Assert.Contains("data is safe", sale.Error.Description);

        // reading your own data and administering users/licenses are declared LicenseRequirement.None
        Assert.True((await service.AuthorizeAsync("reporting.view")).IsSuccess);
        Assert.True((await service.AuthorizeAsync("users.manage")).IsSuccess);
    }

    [Fact]
    public async Task The_grace_period_follows_the_licensing_policy_not_a_decision_of_this_gate()
    {
        var license = new FakeLicense(LicenseState.GracePeriod, "pos");
        var (service, _, _) = Build(license);

        Assert.True((await service.AuthorizeAsync("pos.sale.create")).IsSuccess);
    }

    [Fact]
    public async Task A_state_change_takes_effect_on_the_next_decision()
    {
        var license = new FakeLicense(LicenseState.Active, "pos");
        var (service, _, _) = Build(license);
        Assert.True((await service.AuthorizeAsync("pos.sale.create")).IsSuccess);

        license.State = LicenseState.Expired;
        Assert.True((await service.AuthorizeAsync("pos.sale.create")).IsFailure);

        license.State = LicenseState.Active; // a renewal arrives
        Assert.True((await service.AuthorizeAsync("pos.sale.create")).IsSuccess);
    }

    [Fact]
    public async Task Missing_permission_wins_over_the_license_message_so_unauthorized_users_learn_nothing_about_the_license()
    {
        var license = new FakeLicense(LicenseState.Expired, "pos");
        var (service, permissions, _) = Build(license);
        permissions.Held.Clear();

        var result = await service.AuthorizeAsync("pos.sale.create");

        Assert.Equal(SecurityErrors.ForbiddenCode, result.Error.Code);
        Assert.Equal(0, license.Asked);
    }

    [Fact]
    public async Task A_host_without_a_licensing_component_has_nothing_to_enforce()
    {
        var (service, _, _) = Build(null);

        Assert.True((await service.AuthorizeAsync("pos.sale.create")).IsSuccess);
    }

    [Fact]
    public async Task IsAllowed_applies_the_same_gate_without_recording()
    {
        var (service, _, events) = Build(new FakeLicense(LicenseState.Expired));

        Assert.False(await service.IsAllowedAsync("pos.sale.create"));
        Assert.True(await service.IsAllowedAsync("reporting.view"));
        Assert.Empty(events.Events);
    }
}
