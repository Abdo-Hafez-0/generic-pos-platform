using Client.Licensing.Domain;
using LicenseServer.Application;
using LicenseServer.Infrastructure;
using Licensing.Contracts;
using Platform.Core.Licensing;
using Platform.Core.Modules;

namespace Licensing.Tests;

public sealed class ActivationRenewalTests
{
    private static readonly ModuleId Pos = new("pos");
    private static readonly ModuleId Accounting = new("accounting");

    // --- Installation identity ---

    [Fact]
    public async Task InstallationIdentity_IsGeneratedOnce_AndStaysStable()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();

        var first = await service.GetInstallationIdentityAsync();
        var second = await service.GetInstallationIdentityAsync();

        Assert.NotEqual(Guid.Empty, first.InstallationId);
        Assert.Equal(first, second);
        Assert.Equal(1, world.IdentityStore.Saves);
    }

    [Fact]
    public async Task InstallationIdentity_SurvivesRestart_Reload()
    {
        using var world = new LicensingWorld();
        var before = await world.NewClientService().GetInstallationIdentityAsync();

        var afterRestart = await world.NewClientService().GetInstallationIdentityAsync(); // new service instance, same store

        Assert.Equal(before.InstallationId, afterRestart.InstallationId);
        Assert.Equal(1, world.IdentityStore.Saves);
    }

    [Fact]
    public async Task InstallationIdentity_InvalidStoredValue_IsRegenerated()
    {
        using var world = new LicensingWorld();
        world.IdentityStore.Stored = new InstallationIdentity(Guid.Empty, LicensingWorld.Start);

        var identity = await world.NewClientService().GetInstallationIdentityAsync();

        Assert.NotEqual(Guid.Empty, identity.InstallationId);
    }

    [Fact]
    public void InstallationIdentities_AreUniquePerInstallation()
    {
        var a = InstallationIdentity.CreateNew(LicensingWorld.Start);
        var b = InstallationIdentity.CreateNew(LicensingWorld.Start);

        Assert.NotEqual(a.InstallationId, b.InstallationId);
    }

    // --- Fresh install ---

    [Fact]
    public async Task FreshInstall_IsUnlicensed_AndEntitlementsAreDenied()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();

        var evaluation = await service.InitializeAsync();

        Assert.Equal(LicenseState.Unlicensed, evaluation.State);
        Assert.Equal(LicenseState.Unlicensed, service.State);
        Assert.False(service.IsModuleLicensed(Pos));
    }

    [Fact]
    public void BeforeInitialize_ServiceReportsUnlicensed_WithoutThrowing()
    {
        using var world = new LicensingWorld();

        Assert.Equal(LicenseState.Unlicensed, world.NewClientService().State);
    }

    // --- Activation ---

    [Fact]
    public async Task Activation_VerifiesSignature_StoresLicense_AndEvaluatesActive()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.InitializeAsync();

        var result = await service.ActivateAsync(LicensingWorld.ActivationKey);

        Assert.True(result.IsSuccess);
        Assert.Equal(LicenseState.Active, result.Value.State);
        Assert.NotNull(world.LicenseStore.Stored);                       // stored locally
        Assert.True(service.IsModuleLicensed(Pos));
        Assert.True(service.IsFeatureLicensed(new FeatureId("advancedreports")));
        Assert.False(service.IsModuleLicensed(Accounting));              // not in license; no Accounting module exists
        Assert.False(service.IsFeatureLicensed(new FeatureId("multibranch")));
        Assert.Equal((await service.GetInstallationIdentityAsync()).InstallationId, result.Value.Payload!.InstallationId);
    }

    [Fact]
    public async Task Activation_IssuedLicense_HasLeaseAndGraceFromServerOptions()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();

        var p = (await service.ActivateAsync(LicensingWorld.ActivationKey)).Value.Payload!;

        Assert.Equal(LicensingWorld.Start.AddDays(30), p.LeaseValidUntil);
        Assert.Equal(LicensingWorld.Start.AddDays(37), p.GracePeriodUntil);
        Assert.Equal(1, p.LicenseVersion);
        Assert.Equal("test-key-1", p.KeyId);
        Assert.Equal("Test Issuer", p.Issuer);
    }

    [Fact]
    public async Task Activation_UnknownKey_Fails_AndStoresNothing()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();

        var result = await service.ActivateAsync("NOPE");

        Assert.True(result.IsFailure);
        Assert.Equal(LicenseErrorCodes.NotFound, result.Error.Code);
        Assert.Null(world.LicenseStore.Stored);
        Assert.Equal(LicenseState.Unlicensed, service.State);
    }

    [Fact]
    public async Task Activation_EmptyKey_FailsWithoutCallingServer()
    {
        using var world = new LicensingWorld();

        var result = await world.NewClientService().ActivateAsync("  ");

        Assert.True(result.IsFailure);
        Assert.Equal(0, world.Client.Calls);
    }

    [Fact]
    public async Task Activation_ResponseSignedWithUntrustedKey_IsRejected_NotStored()
    {
        using var world = new LicensingWorld();
        using var attacker = new LicensingWorld(); // different key, same key id
        var service = world.NewClientService(client: attacker.Client);

        var result = await service.ActivateAsync(LicensingWorld.ActivationKey);

        Assert.True(result.IsFailure);
        Assert.Equal("Licensing.Verification.Failed", result.Error.Code);
        Assert.Null(world.LicenseStore.Stored);
        Assert.Equal(LicenseState.Unlicensed, service.State);
    }

    [Fact]
    public async Task Activation_ClientTrustsOnlyItsConfiguredKey_NotJustASuccessfulResponse()
    {
        using var world = new LicensingWorld();
        using var otherKey = EcdsaLicenseSigner.GenerateEphemeral("test-key-1");
        var service = world.NewClientService(verifier: world.NewVerifier(publicKey: otherKey.ExportPublicKey()));

        var result = await service.ActivateAsync(LicensingWorld.ActivationKey);

        Assert.True(result.IsFailure);
        Assert.Null(world.LicenseStore.Stored);
    }

    [Fact]
    public async Task Activation_TamperedResponsePayload_IsRejected()
    {
        using var world = new LicensingWorld();
        world.Client.TamperLicense = l =>
        {
            var p = LicenseSerializer.TryParsePayload(l.Payload)! with { Modules = ["pos", "accounting"] };
            return l with { Payload = LicenseSerializer.ToPayloadText(LicenseSerializer.SerializePayloadBytes(p)) };
        };
        var service = world.NewClientService();

        var result = await service.ActivateAsync(LicensingWorld.ActivationKey);

        Assert.True(result.IsFailure);
        Assert.Null(world.LicenseStore.Stored);
    }

    [Fact]
    public async Task Activation_LicenseForAnotherInstallation_IsRejected()
    {
        using var world = new LicensingWorld();
        // The server (wrongly or maliciously) returns a license bound to a different installation.
        await world.Repository.AddAsync(new LicenseRecord
        {
            LicenseId = Guid.NewGuid(), CustomerId = "c2", ActivationKey = "OTHER-INSTALL", ProductId = LicensingWorld.ProductId,
            ValidFrom = LicensingWorld.Start.AddDays(-1), ValidUntil = LicensingWorld.Start.AddYears(1), Modules = ["pos"], Features = []
        });
        var other = await world.Server.ActivateAsync(new ActivationRequest("OTHER-INSTALL", Guid.NewGuid(), LicensingWorld.ProductId));
        Assert.True(other.IsSuccess);
        world.Client.TamperLicense = _ => other.License!;
        var service = world.NewClientService();

        var result = await service.ActivateAsync(LicensingWorld.ActivationKey);

        Assert.True(result.IsFailure);
        Assert.Equal("Licensing.Verification.Rejected", result.Error.Code);
        Assert.Null(world.LicenseStore.Stored);
    }

    [Fact]
    public async Task Activation_SecondInstallation_IsRefusedByServer()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);

        var secondMachine = new LicensingWorld(); // different identity store; reuse same server via its own client below
        using var _ = secondMachine;
        var service = new Client.Licensing.Application.LicenseService(
            new Client.Licensing.Application.InstallationIdentityService(new InMemoryIdentityStore(), world.Clock),
            new InMemoryLicenseStore(), world.NewVerifier(), world.Client, world.Clock,
            new Client.Licensing.Application.LicensingOptions(LicensingWorld.ProductId), new LicensePolicy());

        var result = await service.ActivateAsync(LicensingWorld.ActivationKey);

        Assert.True(result.IsFailure);
        Assert.Equal(LicenseErrorCodes.AlreadyActivated, result.Error.Code);
    }

    [Fact]
    public async Task Activation_ReactivationOnSameInstallation_IsAllowed_AndIncrementsVersion()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);

        var again = await service.ActivateAsync(LicensingWorld.ActivationKey);

        Assert.True(again.IsSuccess);
        Assert.Equal(2, again.Value.Payload!.LicenseVersion);
    }

    [Fact]
    public async Task Activation_ServerUnreachable_ReturnsFailure_AndChangesNothing()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService(client: new UnreachableLicenseClient());

        var result = await service.ActivateAsync(LicensingWorld.ActivationKey);

        Assert.True(result.IsFailure);
        Assert.Equal(LicenseErrorCodes.ServerUnreachable, result.Error.Code);
        Assert.Null(world.LicenseStore.Stored);
    }

    // --- Offline evaluation / restart ---

    [Fact]
    public async Task Offline_AfterActivation_RestartedClientEvaluatesLicenseWithNoNetwork()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);

        var offline = new UnreachableLicenseClient();
        var restarted = world.NewClientService(client: offline);   // "application restart", Internet OFF
        world.Clock.Advance(TimeSpan.FromDays(10));
        var evaluation = await restarted.InitializeAsync();

        Assert.Equal(LicenseState.Active, evaluation.State);
        Assert.True(restarted.IsModuleLicensed(Pos));
        Assert.Equal(0, offline.Calls);   // evaluation made no network attempt at all
    }

    [Fact]
    public async Task Offline_EntitlementQueries_NeverTouchTheNetwork_AndFollowTheClock()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);
        var offline = new UnreachableLicenseClient();
        var service = world.NewClientService(client: offline);
        await service.InitializeAsync();

        Assert.Equal(LicenseState.Active, service.State);
        world.Clock.Advance(TimeSpan.FromDays(31));    // lease over, grace running
        Assert.Equal(LicenseState.GracePeriod, service.State);
        Assert.True(service.IsModuleLicensed(Pos));
        world.Clock.Advance(TimeSpan.FromDays(7));     // grace over
        Assert.Equal(LicenseState.Expired, service.State);
        Assert.False(service.IsModuleLicensed(Pos));
        Assert.Equal(0, offline.Calls);
    }

    // --- Tampering with local data ---

    [Fact]
    public async Task Tampering_ModifiedLocalLicense_IsRejectedAtLoad()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);
        var stored = world.LicenseStore.Stored!;
        var forged = LicenseSerializer.TryParsePayload(stored.Payload)! with { ValidUntil = LicensingWorld.Start.AddYears(99), LeaseValidUntil = LicensingWorld.Start.AddYears(99), GracePeriodUntil = LicensingWorld.Start.AddYears(99) };
        world.LicenseStore.Stored = stored with { Payload = LicenseSerializer.ToPayloadText(LicenseSerializer.SerializePayloadBytes(forged)) };

        var restarted = world.NewClientService();
        var evaluation = await restarted.InitializeAsync();

        Assert.Equal(LicenseState.Invalid, evaluation.State);
        Assert.Equal(InvalidReason.BadSignature, evaluation.InvalidReason);
        Assert.False(restarted.IsModuleLicensed(Pos));
    }

    [Fact]
    public async Task Tampering_CorruptLocalLicenseFile_IsInvalid_NotACrash()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);
        world.LicenseStore.Corrupt = true;

        var evaluation = await world.NewClientService().InitializeAsync();

        Assert.Equal(LicenseState.Invalid, evaluation.State);
        Assert.Equal(InvalidReason.Malformed, evaluation.InvalidReason);
    }

    [Fact]
    public async Task Tampering_LicenseCopiedToAnotherInstallation_IsInvalid()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);
        var license = world.LicenseStore.Stored!;

        // Another machine (new identity) is handed the same license file.
        var otherMachine = new Client.Licensing.Application.LicenseService(
            new Client.Licensing.Application.InstallationIdentityService(new InMemoryIdentityStore(), world.Clock),
            new InMemoryLicenseStore { Stored = license }, world.NewVerifier(), new UnreachableLicenseClient(), world.Clock,
            new Client.Licensing.Application.LicensingOptions(LicensingWorld.ProductId), new LicensePolicy());

        var evaluation = await otherMachine.InitializeAsync();

        Assert.Equal(LicenseState.Invalid, evaluation.State);
        Assert.Equal(InvalidReason.WrongInstallation, evaluation.InvalidReason);
    }

    [Fact]
    public async Task Tampering_ChangedInstallationIdentity_BreaksBinding()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);
        world.IdentityStore.Stored = InstallationIdentity.CreateNew(LicensingWorld.Start);

        var evaluation = await world.NewClientService().InitializeAsync();

        Assert.Equal(LicenseState.Invalid, evaluation.State);
        Assert.Equal(InvalidReason.WrongInstallation, evaluation.InvalidReason);
    }

    // --- Renewal ---

    [Fact]
    public async Task Renewal_Success_ExtendsLease_IncrementsVersion_AndStores()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        world.Clock.Advance(TimeSpan.FromDays(25));

        var result = await service.RenewAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Payload!.LicenseVersion);
        Assert.Equal(LicensingWorld.Start.AddDays(25 + 30), result.Value.Payload.LeaseValidUntil);
        Assert.Equal(LicenseState.Active, service.State);
        // the stored copy is the renewed one
        Assert.Equal(2, LicenseSerializer.TryParsePayload(world.LicenseStore.Stored!.Payload)!.LicenseVersion);
    }

    [Fact]
    public async Task Renewal_RecoversFromGracePeriod()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        world.Clock.Advance(TimeSpan.FromDays(33));
        Assert.Equal(LicenseState.GracePeriod, service.State);

        var result = await service.RenewAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(LicenseState.Active, service.State);
    }

    [Fact]
    public async Task Renewal_LeaseNeverExceedsCommercialValidity()
    {
        using var world = new LicensingWorld(validUntil: LicensingWorld.Start.AddDays(10));
        var service = world.NewClientService();

        var p = (await service.ActivateAsync(LicensingWorld.ActivationKey)).Value.Payload!;

        Assert.Equal(p.ValidUntil, p.LeaseValidUntil);
        Assert.Equal(p.ValidUntil, p.GracePeriodUntil);
    }

    [Fact]
    public async Task Renewal_WithoutActivation_Fails()
    {
        using var world = new LicensingWorld();

        var result = await world.NewClientService().RenewAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Licensing.Renewal.NoValidLicense", result.Error.Code);
    }

    [Fact]
    public async Task Renewal_ServerUnreachable_KeepsExistingLicense()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);
        var storedBefore = world.LicenseStore.Stored;
        var service = world.NewClientService(client: new UnreachableLicenseClient());
        await service.InitializeAsync();

        var result = await service.RenewAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(LicenseErrorCodes.ServerUnreachable, result.Error.Code);
        Assert.Same(storedBefore, world.LicenseStore.Stored);
        Assert.Equal(LicenseState.Active, service.State);
    }

    [Fact]
    public async Task Renewal_TamperedResponse_IsRejected_AndOldLicenseKept()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        var storedBefore = world.LicenseStore.Stored;
        world.Client.TamperLicense = l => l with { Signature = Convert.ToBase64String(new byte[64]) };

        var result = await service.RenewAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Licensing.Verification.Failed", result.Error.Code);
        Assert.Same(storedBefore, world.LicenseStore.Stored);
    }

    [Fact]
    public async Task Renewal_StaleOrReplayedLicense_IsRejected()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        var v1 = world.LicenseStore.Stored!;
        await service.RenewAsync();   // now v2
        var storedV2 = world.LicenseStore.Stored;
        world.Client.TamperLicense = _ => v1;   // replay of the older (validly signed) license

        var result = await service.RenewAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Licensing.Renewal.Stale", result.Error.Code);
        Assert.Same(storedV2, world.LicenseStore.Stored);
    }

    [Fact]
    public async Task Renewal_DifferentLicense_IsRejected()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        var identity = await service.GetInstallationIdentityAsync();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        var other = new LicenseRecord
        {
            LicenseId = Guid.NewGuid(), CustomerId = "c2", ActivationKey = "OTHER", ProductId = LicensingWorld.ProductId,
            ValidFrom = LicensingWorld.Start.AddDays(-1), ValidUntil = LicensingWorld.Start.AddYears(1), Modules = ["accounting"], Features = []
        };
        await world.Repository.AddAsync(other);
        var otherLicense = (await world.Server.ActivateAsync(new ActivationRequest("OTHER", identity.InstallationId, LicensingWorld.ProductId))).License!;
        world.Client.TamperLicense = _ => otherLicense;

        var result = await service.RenewAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Licensing.Renewal.WrongLicense", result.Error.Code);
        Assert.False(service.IsModuleLicensed(Accounting));
    }

    [Fact]
    public async Task Renewal_OfTamperedLocalLicense_IsRefused()
    {
        using var world = new LicensingWorld();
        await world.NewClientService().ActivateAsync(LicensingWorld.ActivationKey);
        world.LicenseStore.Stored = world.LicenseStore.Stored! with { Signature = Convert.ToBase64String(new byte[64]) };
        var service = world.NewClientService();
        await service.InitializeAsync();

        var result = await service.RenewAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("Licensing.Renewal.NoValidLicense", result.Error.Code);
    }

    // --- Suspension / revocation (server-issued, evaluated locally afterwards) ---

    [Fact]
    public async Task Suspension_ArrivesWithRenewal_AndIsEvaluatedLocally()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        await world.Server.SetStatusAsync(world.Record.LicenseId, LicenseStatusClaim.Suspended);

        var result = await service.RenewAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(LicenseState.Suspended, result.Value.State);
        // after restart, offline, still suspended
        var offline = world.NewClientService(client: new UnreachableLicenseClient());
        Assert.Equal(LicenseState.Suspended, (await offline.InitializeAsync()).State);
        Assert.False(offline.IsModuleLicensed(Pos));
    }

    [Fact]
    public async Task Revocation_ArrivesWithRenewal_AndIsDistinctFromSuspension()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        await world.Server.SetStatusAsync(world.Record.LicenseId, LicenseStatusClaim.Revoked);

        var result = await service.RenewAsync();

        Assert.Equal(LicenseState.Revoked, result.Value.State);
        Assert.False(service.IsModuleLicensed(Pos));
    }

    [Fact]
    public async Task Reinstatement_AfterSuspension_RestoresActive()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        await world.Server.SetStatusAsync(world.Record.LicenseId, LicenseStatusClaim.Suspended);
        await service.RenewAsync();
        await world.Server.SetStatusAsync(world.Record.LicenseId, LicenseStatusClaim.Active);

        var result = await service.RenewAsync();

        Assert.Equal(LicenseState.Active, result.Value.State);
        Assert.True(service.IsModuleLicensed(Pos));
    }

    // --- Policy ---

    [Fact]
    public async Task Policy_GraceRestriction_AppliesThroughTheService()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService(policy: new LicensePolicy(GraceGrantsEntitlements: false));
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        world.Clock.Advance(TimeSpan.FromDays(33));

        Assert.Equal(LicenseState.GracePeriod, service.State);
        Assert.False(service.IsModuleLicensed(Pos));
    }

    // --- Platform abstraction ---

    [Fact]
    public async Task ModuleManifest_LinksToEntitlements_ThroughModuleId()
    {
        using var world = new LicensingWorld();
        var service = world.NewClientService();
        await service.ActivateAsync(LicensingWorld.ActivationKey);

        var posManifest = new FakeManifest("pos");
        var accountingManifest = new FakeManifest("accounting");

        Assert.True(Platform.Application.Abstractions.Licensing.LicenseEntitlementExtensions.IsLicensed(service, posManifest));
        Assert.False(Platform.Application.Abstractions.Licensing.LicenseEntitlementExtensions.IsLicensed(service, accountingManifest));
    }

    private sealed class FakeManifest(string id) : IModuleManifest
    {
        public ModuleId ModuleId { get; } = new(id);
        public string Name => id;
        public ModuleVersion Version => new(1, 0, 0);
        public string Publisher => "test";
        public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
        public ModuleVersion? MaximumPlatformVersion => null;
        public IReadOnlyList<ModuleDependency> Dependencies => [];
        public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures => [];
        public int DatabaseSchemaVersion => 1;
    }
}
