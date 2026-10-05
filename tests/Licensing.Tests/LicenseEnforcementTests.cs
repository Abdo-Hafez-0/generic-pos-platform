using Client.Licensing.Application;
using Client.Licensing.Domain;
using Licensing.Contracts;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;
using Platform.Core.Licensing;

namespace Licensing.Tests;

/// <summary>
/// The real licensing client (real signatures, real lease/grace evaluator, manual clock) behind the real authorization service:
/// what a user can START follows the signed license, offline, and the license never has any way to touch data.
/// </summary>
public sealed class LicenseEnforcementTests
{
    private const string Sell = "pos.sale.create";
    private const string Adjust = "inventory.stock.adjust";
    private const string Reports = "reporting.view";

    private sealed class Provider : ICapabilityProvider
    {
        public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() =>
        [
            new(Sell, "pos", "Sell", "d"),
            new(Adjust, "inventory", "Adjust stock", "d"),
            new(Reports, "reporting", "Reports", "d", LicenseRequirement.None),
            .. LicensingCapabilities.All
        ];
    }

    private sealed class AllowEverything : IPermissionProvider
    {
        public Task<IReadOnlyCollection<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<string>>([Sell, Adjust, Reports, LicensingCapabilities.Manage]);
    }

    private sealed class Sink : ISecurityEventSink
    {
        public List<SecurityEvent> Events { get; } = [];

        public Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(securityEvent);
            return Task.CompletedTask;
        }
    }

    private static AuthorizationService Authorization(LicenseService service, Sink? sink = null)
    {
        var session = new SessionContext();
        session.SignIn(new AuthenticatedIdentity(Guid.NewGuid(), "admin", "Admin"));
        return new AuthorizationService(session, new CapabilityCatalog([new Provider()]), new AllowEverything(), sink, service);
    }

    [Fact]
    public async Task A_valid_license_allows_the_licensed_modules_and_only_those()
    {
        using var world = new LicensingWorld(modules: ["pos"]);
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        var authorization = Authorization(service);

        Assert.True((await authorization.AuthorizeAsync(Sell)).IsSuccess);
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, (await authorization.AuthorizeAsync(Adjust)).Error.Code); // restricted entitlement
    }

    [Fact]
    public async Task No_license_at_all_restricts_licensed_work_but_not_reading_your_data()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.InitializeAsync();
        var authorization = Authorization(service);

        Assert.Equal(LicenseState.Unlicensed, service.State);
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, (await authorization.AuthorizeAsync(Sell)).Error.Code);
        Assert.True((await authorization.AuthorizeAsync(Reports)).IsSuccess);
        Assert.True((await authorization.AuthorizeAsync(LicensingCapabilities.Manage)).IsSuccess); // the way out stays open
    }

    [Fact]
    public async Task An_expired_license_restricts_selling_and_a_renewal_restores_it()
    {
        using var world = new LicensingWorld(validUntil: LicensingWorld.Start.AddDays(60));
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        var authorization = Authorization(service);
        Assert.True((await authorization.AuthorizeAsync(Sell)).IsSuccess);

        world.Clock.Advance(TimeSpan.FromDays(61)); // past ValidUntil
        Assert.Equal(LicenseState.Expired, service.State);
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, (await authorization.AuthorizeAsync(Sell)).Error.Code);
        Assert.True((await authorization.AuthorizeAsync(Reports)).IsSuccess);

        world.Record.ValidUntil = LicensingWorld.Start.AddYears(2); // the vendor extends the license ...
        Assert.True((await service.RenewAsync()).IsSuccess);        // ... and the next renewal brings it
        Assert.True((await authorization.AuthorizeAsync(Sell)).IsSuccess);
    }

    [Fact]
    public async Task The_offline_lease_keeps_working_without_the_server_then_grace_then_it_stops()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        var offline = world.NewClientService(client: new UnreachableLicenseClient());
        await offline.InitializeAsync(); // a restart with the Internet off
        var authorization = Authorization(offline);

        world.Clock.Advance(TimeSpan.FromDays(29));    // inside the 30-day lease
        Assert.Equal(LicenseState.Active, offline.State);
        Assert.True((await authorization.AuthorizeAsync(Sell)).IsSuccess);

        world.Clock.Advance(TimeSpan.FromDays(3));     // lease over, inside the 7-day grace
        Assert.Equal(LicenseState.GracePeriod, offline.State);
        Assert.True((await authorization.AuthorizeAsync(Sell)).IsSuccess);

        world.Clock.Advance(TimeSpan.FromDays(10));    // grace over, never renewed
        Assert.Equal(LicenseState.Expired, offline.State);
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, (await authorization.AuthorizeAsync(Sell)).Error.Code);
    }

    [Fact]
    public async Task A_forged_local_license_is_rejected_and_nothing_licensed_is_allowed()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);
        var stored = world.LicenseStore.Stored!;
        var forged = LicenseSerializer.TryParsePayload(stored.Payload)! with { Modules = ["pos", "inventory", "everything"], ValidUntil = LicensingWorld.Start.AddYears(99) };
        world.LicenseStore.Stored = stored with { Payload = LicenseSerializer.ToPayloadText(LicenseSerializer.SerializePayloadBytes(forged)) };
        var restarted = world.NewClientService();
        await restarted.InitializeAsync();
        var authorization = Authorization(restarted);

        Assert.Equal(LicenseState.Invalid, restarted.State);
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, (await authorization.AuthorizeAsync(Sell)).Error.Code);
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, (await authorization.AuthorizeAsync(Adjust)).Error.Code);
    }

    [Fact]
    public async Task A_license_for_another_installation_and_one_signed_by_an_untrusted_key_allow_nothing()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);
        var license = world.LicenseStore.Stored!;

        var otherMachine = new LicenseService(
            new InstallationIdentityService(new InMemoryIdentityStore(), world.Clock),
            new InMemoryLicenseStore { Stored = license }, world.NewVerifier(), new UnreachableLicenseClient(), world.Clock,
            new LicensingOptions(LicensingWorld.ProductId), new LicensePolicy());
        await otherMachine.InitializeAsync();

        using var stranger = new LicensingWorld();
        var strangerKey = world.NewClientService(verifier: world.NewVerifier(stranger.Signer.ExportPublicKey(), "some-other-key"));   // trusts a different key than the one that signed
        await strangerKey.InitializeAsync();

        Assert.Equal(InvalidReason.WrongInstallation, otherMachine.Current.InvalidReason);
        Assert.Equal(InvalidReason.UntrustedKey, strangerKey.Current.InvalidReason);
        Assert.False(await Authorization(otherMachine).IsAllowedAsync(Sell));
        Assert.False(await Authorization(strangerKey).IsAllowedAsync(Sell));
    }

    [Fact]
    public async Task A_suspended_or_revoked_license_restricts_work_after_the_next_renewal()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        var authorization = Authorization(service);

        world.Record.Status = LicenseStatusClaim.Suspended;
        await service.RenewAsync();

        Assert.Equal(LicenseState.Suspended, service.State);
        Assert.False(await authorization.IsAllowedAsync(Sell));
        Assert.True(await authorization.IsAllowedAsync(Reports));
    }

    [Fact]
    public async Task Licensing_has_no_way_to_touch_business_data_an_expired_license_only_declines_to_start_work()
    {
        // The license store and the business database are separate worlds: an expired license leaves the stored license
        // file intact (it is how a renewal is recognised) and the evaluation is a pure function with no data access at all.
        using var world = new LicensingWorld(validUntil: LicensingWorld.Start.AddDays(10));
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);

        world.Clock.Advance(TimeSpan.FromDays(400));

        Assert.Equal(LicenseState.Expired, service.State);
        Assert.NotNull(world.LicenseStore.Stored);
        Assert.NotNull((await world.IdentityStore.LoadAsync()));
    }

    // ------------------------------------------------------------------ managing the license is itself protected

    [Fact]
    public async Task Activating_needs_licensing_manage_and_the_key_is_not_even_sent_without_it()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        var denied = new ScriptedAuthorization();
        var handler = new ActivateLicenseCommandHandler(service, denied);

        var refused = await handler.HandleAsync(new ActivateLicenseCommand(LicensingWorld.ActivationKey));

        Assert.True(refused.IsFailure);
        Assert.Equal(SecurityErrors.ForbiddenCode, refused.Error.Code);
        Assert.Equal(0, world.Client.Calls);          // nothing reached the license server
        denied.Allow = true;
        Assert.True((await handler.HandleAsync(new ActivateLicenseCommand(LicensingWorld.ActivationKey))).IsSuccess);
        Assert.Equal(1, world.Client.Calls);
    }

    [Fact]
    public async Task Renewing_by_hand_needs_licensing_manage_too()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        var calls = world.Client.Calls;
        var denied = new ScriptedAuthorization();

        var refused = await new RenewLicenseCommandHandler(service, denied).HandleAsync(new RenewLicenseCommand());

        Assert.Equal(SecurityErrors.ForbiddenCode, refused.Error.Code);
        Assert.Equal(calls, world.Client.Calls);
    }

    private sealed class ScriptedAuthorization : IAuthorizationService
    {
        public bool Allow { get; set; }

        public Task<Platform.Core.Results.Result> AuthorizeAsync(string capability, CancellationToken cancellationToken = default)
            => Task.FromResult(Allow ? Platform.Core.Results.Result.Success() : SecurityErrors.Forbidden(capability));

        public Task<bool> IsAllowedAsync(string capability, CancellationToken cancellationToken = default) => Task.FromResult(Allow);
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task License_decisions_are_audited_and_never_carry_the_activation_key_or_license_content()
    {
        using var world = new LicensingWorld();
        var sink = new Sink();
        var service = new LicenseService(
            new InstallationIdentityService(world.IdentityStore, world.Clock), world.LicenseStore, world.NewVerifier(), world.Client, world.Clock,
            new LicensingOptions(LicensingWorld.ProductId), new LicensePolicy(), sink);

        await service.InitializeAsync();
        await service.ActivateAsync("WRONG-ACTIVATION-KEY");              // the server refuses
        await service.ActivateAsync(LicensingWorld.ActivationKey);        // accepted
        world.Clock.Advance(TimeSpan.FromDays(1));
        await service.RenewAsync();                                        // accepted
        var license = world.LicenseStore.Stored!;
        var tampered = new LicenseService(
            new InstallationIdentityService(world.IdentityStore, world.Clock),
            new InMemoryLicenseStore { Stored = license with { Signature = Convert.ToBase64String(new byte[64]) } },
            world.NewVerifier(), world.Client, world.Clock, new LicensingOptions(LicensingWorld.ProductId), new LicensePolicy(), sink);
        await tampered.InitializeAsync();                                  // forged signature

        var actions = sink.Events.Select(e => e.Action).ToList();
        Assert.Contains("security.license.loaded", actions);
        Assert.Contains("security.license.activation-failed", actions);
        Assert.Contains("security.license.activated", actions);
        Assert.Contains("security.license.renewed", actions);
        Assert.Contains(sink.Events, e => e.Action == "security.license.loaded" && e.Outcome == SecurityEventOutcome.Denied);

        var everything = string.Join("\n", sink.Events.Select(e => $"{e.Action}|{e.SubjectId}|{e.Summary}|{e.ActorName}"));
        Assert.DoesNotContain(LicensingWorld.ActivationKey, everything);
        Assert.DoesNotContain("WRONG-ACTIVATION-KEY", everything);
        Assert.DoesNotContain(license.Payload, everything);
        Assert.DoesNotContain(license.Signature, everything);
    }

    [Fact]
    public async Task A_replayed_older_renewal_is_audited_as_rejected()
    {
        using var world = new LicensingWorld();
        var sink = new Sink();
        var service = new LicenseService(
            new InstallationIdentityService(world.IdentityStore, world.Clock), world.LicenseStore, world.NewVerifier(), world.Client, world.Clock,
            new LicensingOptions(LicensingWorld.ProductId), new LicensePolicy(), sink);
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        var first = world.LicenseStore.Stored!;
        await service.RenewAsync();
        world.Client.TamperLicense = _ => first;                           // a hostile server (or proxy) replays the older license

        var replay = await service.RenewAsync();

        Assert.True(replay.IsFailure);
        Assert.Contains(sink.Events, e => e.Action == "security.license.rejected" && e.Summary!.Contains("replay"));
    }
}
