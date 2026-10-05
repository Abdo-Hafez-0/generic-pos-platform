using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;
using Platform.Core.Results;

namespace Security.Tests.Foundation;

internal sealed class FakeCapabilityProvider(params CapabilityDescriptor[] descriptors) : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => descriptors;
}

internal sealed class FakePermissionProvider : IPermissionProvider
{
    public Dictionary<Guid, HashSet<string>> Held { get; } = [];

    public int Lookups { get; private set; }

    public Task<IReadOnlyCollection<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        Lookups++;
        return Task.FromResult<IReadOnlyCollection<string>>(Held.TryGetValue(userId, out var set) ? set.ToList() : []);
    }
}

internal sealed class RecordingSink : ISecurityEventSink
{
    public List<SecurityEvent> Events { get; } = [];

    public Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(securityEvent);
        return Task.CompletedTask;
    }
}

public sealed class AuthorizationFoundationTests
{
    private const string Sell = "pos.sale.create";
    private const string Adjust = "inventory.stock.adjust";

    private static readonly CapabilityDescriptor[] Descriptors =
    [
        new(Sell, "pos", "Create sale", "Sell items"),
        new(Adjust, "inventory", "Adjust stock", "Change stock levels", IsSensitive: true)
    ];

    private readonly SessionContext _session = new();
    private readonly FakePermissionProvider _permissions = new();
    private readonly RecordingSink _events = new();
    private readonly AuthorizationService _authorization;
    private readonly Guid _cashier = Guid.NewGuid();

    public AuthorizationFoundationTests()
    {
        _authorization = new AuthorizationService(
            _session, new CapabilityCatalog([new FakeCapabilityProvider(Descriptors)]), _permissions, _events);
    }

    private void SignIn(params string[] held)
    {
        _permissions.Held[_cashier] = [.. held];
        _session.SignIn(new AuthenticatedIdentity(_cashier, "cashier1", "Cashier One"));
    }

    [Fact]
    public async Task Authorized_operation_succeeds()
    {
        SignIn(Sell);

        var result = await _authorization.AuthorizeAsync(Sell);

        Assert.True(result.IsSuccess);
        Assert.Empty(_events.Events);
    }

    [Fact]
    public async Task Unauthorized_operation_is_refused_and_recorded()
    {
        SignIn(Sell);

        var result = await _authorization.AuthorizeAsync(Adjust);

        Assert.True(result.IsFailure);
        Assert.Equal(SecurityErrors.ForbiddenCode, result.Error.Code);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
        var recorded = Assert.Single(_events.Events);
        Assert.Equal("security.authorization.denied", recorded.Action);
        Assert.Equal(SecurityEventOutcome.Denied, recorded.Outcome);
        Assert.Equal(_cashier, recorded.ActorId);
        Assert.Equal(Adjust, recorded.SubjectId);
    }

    [Fact]
    public async Task Nobody_signed_in_is_refused()
    {
        _permissions.Held[_cashier] = [Sell];

        var result = await _authorization.AuthorizeAsync(Sell);

        Assert.Equal(SecurityErrors.NotAuthenticatedCode, result.Error.Code);
        Assert.Equal(0, _permissions.Lookups);
    }

    [Fact]
    public async Task Signing_out_removes_access_immediately()
    {
        SignIn(Sell);
        Assert.True((await _authorization.AuthorizeAsync(Sell)).IsSuccess);

        _session.SignOut();

        Assert.Equal(SecurityErrors.NotAuthenticatedCode, (await _authorization.AuthorizeAsync(Sell)).Error.Code);
    }

    [Fact]
    public async Task Unknown_capability_is_refused_even_for_a_user_who_holds_the_code()
    {
        SignIn("pos.invented.thing");

        var result = await _authorization.AuthorizeAsync("pos.invented.thing");

        Assert.Equal(SecurityErrors.UnknownCapabilityCode, result.Error.Code);
    }

    [Fact]
    public async Task Capability_changes_take_effect_on_the_next_check_without_signing_in_again()
    {
        SignIn(Sell);
        Assert.True((await _authorization.AuthorizeAsync(Sell)).IsSuccess);
        Assert.True((await _authorization.AuthorizeAsync(Adjust)).IsFailure);

        _permissions.Held[_cashier].Add(Adjust);
        _permissions.Held[_cashier].Remove(Sell);

        Assert.True((await _authorization.AuthorizeAsync(Adjust)).IsSuccess);
        Assert.True((await _authorization.AuthorizeAsync(Sell)).IsFailure);
    }

    [Fact]
    public async Task A_user_who_lost_every_permission_is_refused()
    {
        SignIn(Sell);
        _permissions.Held[_cashier].Clear(); // e.g. the user was deactivated: the provider returns nothing

        Assert.True((await _authorization.AuthorizeAsync(Sell)).IsFailure);
    }

    [Fact]
    public async Task Without_a_permission_source_everything_is_refused()
    {
        var failClosed = new AuthorizationService(_session, new CapabilityCatalog([new FakeCapabilityProvider(Descriptors)]));
        SignIn(Sell);

        Assert.Equal(SecurityErrors.ForbiddenCode, (await failClosed.AuthorizeAsync(Sell)).Error.Code);
    }

    [Fact]
    public async Task IsAllowed_answers_without_recording_anything()
    {
        SignIn(Sell);

        Assert.True(await _authorization.IsAllowedAsync(Sell));
        Assert.False(await _authorization.IsAllowedAsync(Adjust));
        Assert.Empty(_events.Events);
    }

    [Fact]
    public async Task Capability_codes_compare_case_insensitively()
    {
        SignIn(Sell);

        Assert.True((await _authorization.AuthorizeAsync("POS.Sale.Create")).IsSuccess);
    }
}

public sealed class SessionContextTests
{
    [Fact]
    public void Starts_unauthenticated_with_an_empty_identity()
    {
        var session = new SessionContext();

        Assert.False(session.IsAuthenticated);
        Assert.Equal(Guid.Empty, session.UserId);
        Assert.Equal(string.Empty, session.UserName);
    }

    [Fact]
    public void Sign_in_sets_the_identity_and_sign_out_clears_it()
    {
        var session = new SessionContext();
        var id = Guid.NewGuid();

        session.SignIn(new AuthenticatedIdentity(id, "ann", "Ann"));
        Assert.True(session.IsAuthenticated);
        Assert.Equal(id, session.UserId);
        Assert.Equal("ann", session.UserName);
        Assert.Equal("Ann", session.DisplayName);

        session.SignOut();
        Assert.False(session.IsAuthenticated);
        Assert.Equal(Guid.Empty, session.UserId);
    }

    [Fact]
    public void An_identity_without_an_id_cannot_sign_in()
    {
        var session = new SessionContext();

        Assert.Throws<ArgumentException>(() => session.SignIn(new AuthenticatedIdentity(Guid.Empty, "x", "X")));
        Assert.False(session.IsAuthenticated);
    }
}

public sealed class CapabilityCatalogTests
{
    [Fact]
    public void Finds_declared_capabilities_and_lists_them_in_code_order()
    {
        var catalog = new CapabilityCatalog([
            new FakeCapabilityProvider(new CapabilityDescriptor("pos.sale.create", "pos", "Create", "d")),
            new FakeCapabilityProvider(new CapabilityDescriptor("audit.view", "audit", "View", "d", LicenseRequirement.None))]);

        Assert.Equal(["audit.view", "pos.sale.create"], catalog.All.Select(c => c.Code).ToArray());
        Assert.NotNull(catalog.Find("POS.SALE.CREATE"));
        Assert.Null(catalog.Find("nope.nope"));
        Assert.Null(catalog.Find(""));
    }

    [Fact]
    public void A_duplicate_code_is_a_programming_error()
    {
        var d = new CapabilityDescriptor("pos.sale.create", "pos", "Create", "d");

        Assert.Throws<InvalidOperationException>(() => new CapabilityCatalog([new FakeCapabilityProvider(d, d)]));
    }

    [Theory]
    [InlineData("Pos.Sale")]
    [InlineData("pos")]
    [InlineData("pos..sale")]
    [InlineData("pos.sale create")]
    [InlineData("")]
    public void An_invalid_code_is_rejected(string code)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CapabilityCatalog([new FakeCapabilityProvider(new CapabilityDescriptor(code, "pos", "n", "d"))]));
    }

    [Fact]
    public void A_capability_must_name_its_module()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CapabilityCatalog([new FakeCapabilityProvider(new CapabilityDescriptor("pos.sale.create", " ", "n", "d"))]));
    }
}
