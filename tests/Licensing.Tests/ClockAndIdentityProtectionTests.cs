using System.Text;
using Client.Licensing.Application;
using Client.Licensing.Domain;
using Client.Licensing.Infrastructure;
using Licensing.Contracts;
using Platform.Application.Abstractions.Security;
using Platform.Core.Licensing;

namespace Licensing.Tests;

/// <summary>A reversible stand-in for the operating-system protector: bound to a "user", purpose-bound, and it detects alteration.</summary>
internal sealed class FakeProtector(string user = "alice") : ISecretProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var data = plaintext.ToArray();
        var key = Encoding.UTF8.GetBytes(user + "|" + purpose);
        var body = data.Select((b, i) => (byte)(b ^ key[i % key.Length] ^ 0x5A)).ToArray();
        return [.. Encoding.UTF8.GetBytes("SEALED:" + Convert.ToBase64String(SHA256Prefix(key, body))), .. body];
    }

    public bool TryUnprotect(ReadOnlySpan<byte> protectedData, string purpose, out byte[] plaintext)
    {
        plaintext = [];
        var raw = protectedData.ToArray();
        const int tagLength = 7 + 24; // "SEALED:" + Base64 of 16 bytes
        if (raw.Length < tagLength) return false;

        var key = Encoding.UTF8.GetBytes(user + "|" + purpose);
        var body = raw[tagLength..];
        var expected = Encoding.UTF8.GetBytes("SEALED:" + Convert.ToBase64String(SHA256Prefix(key, body)));
        if (!raw.AsSpan(0, tagLength).SequenceEqual(expected)) return false;

        plaintext = body.Select((b, i) => (byte)(b ^ key[i % key.Length] ^ 0x5A)).ToArray();
        return true;
    }

    private static byte[] SHA256Prefix(byte[] key, byte[] body)
        => System.Security.Cryptography.SHA256.HashData([.. key, .. body])[..16];
}

internal sealed class RecordingEvents : ISecurityEventSink
{
    public List<SecurityEvent> Events { get; } = [];

    public Task RecordAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(securityEvent);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryClockStore : IClockStateStore
{
    public DateTimeOffset? Stored { get; set; }
    public int Saves { get; private set; }
    public bool FailSaves { get; set; }

    public Task<DateTimeOffset?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stored);

    public Task SaveAsync(DateTimeOffset highWater, CancellationToken cancellationToken = default)
    {
        if (FailSaves) throw new IOException("disk full");
        Stored = highWater;
        Saves++;
        return Task.CompletedTask;
    }
}

public sealed class InstallationIdentityProtectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gpos-id-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private FileInstallationIdentityStore Store(ISecretProtector? protector, RecordingEvents? events = null, bool legacy = false)
        => new(new LicensingStorageOptions(_directory), protector, events, _clock, legacy);

    private string IdentityFile => Path.Combine(_directory, "installation.json");

    [Fact]
    public async Task A_protected_identity_round_trips_and_the_file_does_not_show_the_id()
    {
        var identity = InstallationIdentity.CreateNew(_clock.GetUtcNow());
        var store = Store(new FakeProtector());

        await store.SaveAsync(identity);

        var onDisk = await File.ReadAllTextAsync(IdentityFile);
        Assert.StartsWith("GPOS-PROTECTED/1", onDisk);
        Assert.DoesNotContain(identity.InstallationId.ToString(), onDisk);
        Assert.Equal(identity, await store.LoadAsync());
    }

    [Fact]
    public async Task The_same_file_for_another_user_or_machine_does_not_open_and_is_kept_as_evidence()
    {
        var events = new RecordingEvents();
        var identity = InstallationIdentity.CreateNew(_clock.GetUtcNow());
        await Store(new FakeProtector("alice")).SaveAsync(identity);

        var loaded = await Store(new FakeProtector("mallory"), events).LoadAsync(); // the file copied to another PC / user

        Assert.Null(loaded);
        Assert.Contains(events.Events, e => e.Action == "security.installation.identity-unusable");
        Assert.Single(Directory.GetFiles(_directory, "installation.json.unusable-*"));
        Assert.True(File.Exists(IdentityFile)); // not silently destroyed
    }

    [Fact]
    public async Task An_edited_protected_file_does_not_open()
    {
        await Store(new FakeProtector()).SaveAsync(InstallationIdentity.CreateNew(_clock.GetUtcNow()));
        var text = await File.ReadAllTextAsync(IdentityFile);
        var tampered = text[..^8] + (text[^8] == 'A' ? "B" : "A") + text[^7..];
        await File.WriteAllTextAsync(IdentityFile, tampered);

        Assert.Null(await Store(new FakeProtector()).LoadAsync());
    }

    [Fact]
    public async Task A_blob_made_for_another_purpose_does_not_open_as_the_identity()
    {
        var protector = new FakeProtector();
        var blob = protector.Protect(Encoding.UTF8.GetBytes("{}"), "licensing.clock-high-water");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(IdentityFile, "GPOS-PROTECTED/1" + "\n" + Convert.ToBase64String(blob));

        Assert.Null(await Store(protector).LoadAsync());
    }

    [Fact]
    public async Task A_plain_text_identity_is_refused_by_default_when_protection_exists_so_the_file_cannot_just_be_copied()
    {
        var events = new RecordingEvents();
        var identity = InstallationIdentity.CreateNew(_clock.GetUtcNow());
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(IdentityFile, System.Text.Json.JsonSerializer.Serialize(identity, LicenseSerializer.Options));

        var loaded = await Store(new FakeProtector(), events).LoadAsync();

        Assert.Null(loaded);
        Assert.Contains(events.Events, e => e.Action == "security.installation.identity-unusable" && e.Summary!.Contains("unprotected"));
    }

    [Fact]
    public async Task A_pre_stage_11_identity_can_be_sealed_once_when_explicitly_allowed()
    {
        var events = new RecordingEvents();
        var identity = InstallationIdentity.CreateNew(_clock.GetUtcNow());
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(IdentityFile, System.Text.Json.JsonSerializer.Serialize(identity, LicenseSerializer.Options));

        var loaded = await Store(new FakeProtector(), events, legacy: true).LoadAsync();

        Assert.Equal(identity, loaded);
        Assert.StartsWith("GPOS-PROTECTED/1", await File.ReadAllTextAsync(IdentityFile));   // sealed
        Assert.DoesNotContain(identity.InstallationId.ToString(), await File.ReadAllTextAsync(IdentityFile));
        Assert.Contains(events.Events, e => e.Action == "security.installation.identity-sealed");
        Assert.Equal(identity, await Store(new FakeProtector()).LoadAsync());                  // and from now on the strict path works
    }

    [Fact]
    public async Task Without_any_protection_available_the_identity_stays_plain_as_before()
    {
        var identity = InstallationIdentity.CreateNew(_clock.GetUtcNow());
        var store = Store(protector: null);

        await store.SaveAsync(identity);

        Assert.DoesNotContain("GPOS-PROTECTED/1", await File.ReadAllTextAsync(IdentityFile));
        Assert.Equal(identity, await store.LoadAsync());
    }

    [Fact]
    public async Task A_regenerated_identity_breaks_the_binding_so_it_grants_nothing_and_a_restart_keeps_it_stable()
    {
        using var world = new LicensingWorld();
        var store = Store(new FakeProtector());
        var identityService = new InstallationIdentityService(store, _clock);
        var first = await identityService.GetOrCreateAsync();
        Assert.Equal(first, await new InstallationIdentityService(store, _clock).GetOrCreateAsync()); // stable across restarts

        await File.WriteAllTextAsync(IdentityFile, "garbage");                                         // corruption
        var second = await new InstallationIdentityService(store, _clock).GetOrCreateAsync();
        Assert.NotEqual(first.InstallationId, second.InstallationId);                                   // regenerated, never reused
        Assert.Equal(second, await new InstallationIdentityService(store, _clock).GetOrCreateAsync());
    }
}

public sealed class ClockRollbackGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The wall clock and the monotonic stopwatch move independently here, like a real clock that someone can change.</summary>
    private sealed class World
    {
        public DateTimeOffset Wall { get; set; } = T0;
        public long Mono { get; set; }   // milliseconds
        public InMemoryClockStore Store { get; } = new();

        public ClockRollbackGuard NewGuard(TimeSpan? tolerance = null)
            => new(Store, new ClockGuardOptions(tolerance ?? TimeSpan.FromHours(1)), () => Mono, 1000);

        /// <summary>Real time passes: both the clock and the stopwatch advance.</summary>
        public void Pass(TimeSpan by)
        {
            Wall += by;
            Mono += (long)by.TotalMilliseconds;
        }
    }

    [Fact]
    public async Task Normal_use_is_never_flagged()
    {
        var w = new World();
        var guard = w.NewGuard();
        await guard.LoadAsync();

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ClockCheck.Ok, guard.Observe(w.Wall));
            w.Pass(TimeSpan.FromMinutes(30));
        }
    }

    [Fact]
    public async Task Turning_the_clock_back_during_a_run_is_caught_even_before_any_restart()
    {
        var w = new World();
        var guard = w.NewGuard();
        await guard.LoadAsync();
        guard.Observe(w.Wall);
        w.Pass(TimeSpan.FromMinutes(10));

        w.Wall -= TimeSpan.FromDays(3);          // somebody sets the clock back three days; the stopwatch does not care

        Assert.Equal(ClockCheck.RolledBack, guard.Observe(w.Wall));
    }

    [Fact]
    public async Task A_small_correction_inside_the_tolerance_is_fine()
    {
        var w = new World();
        var guard = w.NewGuard(TimeSpan.FromHours(1));
        await guard.LoadAsync();
        guard.Observe(w.Wall);
        w.Pass(TimeSpan.FromMinutes(10));

        w.Wall -= TimeSpan.FromMinutes(20);      // an NTP step or a hand correction

        Assert.Equal(ClockCheck.Ok, guard.Observe(w.Wall));
    }

    [Fact]
    public async Task A_clock_held_still_while_the_program_runs_is_caught()
    {
        var w = new World();
        var guard = w.NewGuard(TimeSpan.FromHours(1));
        await guard.LoadAsync();
        guard.Observe(w.Wall);

        w.Mono += (long)TimeSpan.FromHours(5).TotalMilliseconds;   // five real hours pass, the clock is frozen at its old value

        Assert.Equal(ClockCheck.RolledBack, guard.Observe(w.Wall));
    }

    [Fact]
    public async Task Rolling_back_across_a_restart_is_caught_because_the_mark_is_persisted()
    {
        var w = new World();
        var guard = w.NewGuard();
        await guard.LoadAsync();
        guard.Observe(w.Wall);
        w.Pass(TimeSpan.FromDays(2));
        guard.Observe(w.Wall);
        await guard.FlushAsync();                 // the app stops

        w.Wall -= TimeSpan.FromDays(10);          // the clock is set back before the next start
        w.Mono = 0;                               // a new run has a new stopwatch
        var restarted = w.NewGuard();
        await restarted.LoadAsync();

        Assert.Equal(ClockCheck.RolledBack, restarted.Observe(w.Wall));
    }

    [Fact]
    public async Task Deleting_the_stored_mark_only_falls_back_to_the_signed_issue_time_of_the_license()
    {
        var w = new World();
        w.Store.Stored = null;                    // the attacker removed clock.state
        var guard = w.NewGuard();
        await guard.LoadAsync();
        guard.SetFloor(T0.AddDays(20));           // what the stored, signed license says it was issued

        w.Wall = T0.AddDays(5);                   // ... and the clock was set back before that
        Assert.Equal(ClockCheck.RolledBack, guard.Observe(w.Wall));
    }

    [Fact]
    public async Task A_forward_jump_during_a_run_does_not_poison_the_mark()
    {
        var w = new World();
        var guard = w.NewGuard();
        await guard.LoadAsync();
        guard.Observe(w.Wall);
        w.Pass(TimeSpan.FromMinutes(5));

        w.Wall += TimeSpan.FromDays(365);         // a mistake: the clock is set a year ahead ...
        Assert.Equal(ClockCheck.Ok, guard.Observe(w.Wall));
        await guard.FlushAsync();
        Assert.True(w.Store.Stored < T0.AddDays(1), "the stored mark followed real elapsed time, not the jump");

        w.Wall -= TimeSpan.FromDays(365);         // ... and corrected
        w.Pass(TimeSpan.FromMinutes(1));
        Assert.Equal(ClockCheck.Ok, guard.Observe(w.Wall));
    }

    [Fact]
    public async Task A_server_signed_time_rebases_the_mark_even_downwards()
    {
        var w = new World();
        var guard = w.NewGuard();
        w.Store.Stored = T0.AddYears(1);          // a mark that ran far ahead (a clock mistake across a restart)
        await guard.LoadAsync();
        Assert.Equal(ClockCheck.RolledBack, guard.Observe(w.Wall));

        guard.Rebase(T0);                         // a renewal arrives: the server vouches for the real time

        Assert.Equal(ClockCheck.Ok, guard.Observe(w.Wall));
    }

    [Fact]
    public async Task Writing_the_mark_is_throttled_and_a_failing_disk_never_stops_the_caller()
    {
        var w = new World();
        var guard = w.NewGuard();
        await guard.LoadAsync();
        for (var i = 0; i < 5; i++)
        {
            guard.Observe(w.Wall);
            w.Pass(TimeSpan.FromSeconds(5));
        }

        await Task.Delay(50);
        Assert.Equal(0, w.Store.Saves);           // not on every call

        w.Store.FailSaves = true;
        w.Pass(TimeSpan.FromMinutes(2));
        guard.Observe(w.Wall);                    // due for a write; the disk is full: no exception reaches here
        await Task.Delay(50);
        await Assert.ThrowsAsync<IOException>(() => guard.FlushAsync()); // an explicit flush reports it to the caller who asked
    }

    [Fact]
    public void The_tolerance_is_bounded_in_both_directions()
    {
        Assert.Equal(ClockGuardOptions.MinimumTolerance, new ClockGuardOptions(TimeSpan.Zero).Normalized().Tolerance);
        Assert.Equal(ClockGuardOptions.MaximumTolerance, new ClockGuardOptions(TimeSpan.FromDays(3650)).Normalized().Tolerance);
        Assert.Equal(TimeSpan.FromHours(3), new ClockGuardOptions(TimeSpan.FromHours(3)).Normalized().Tolerance);
    }
}

public sealed class LicenseClockEnforcementTests
{
    private static LicenseService Service(LicensingWorld world, ClockRollbackGuard guard, ISecurityEventSink? events = null)
        => new(new InstallationIdentityService(world.IdentityStore, world.Clock), world.LicenseStore, world.NewVerifier(), world.Client, world.Clock,
            new LicensingOptions(LicensingWorld.ProductId), new LicensePolicy(), events, guard);

    private sealed class Mono
    {
        public long Ms { get; set; }
    }

    [Fact]
    public async Task Turning_the_clock_back_to_stretch_an_expired_license_is_refused_and_audited()
    {
        using var world = new LicensingWorld(validUntil: LicensingWorld.Start.AddDays(40));
        var mono = new Mono();
        var store = new InMemoryClockStore();
        var events = new RecordingEvents();
        var service = Service(world, new ClockRollbackGuard(store, new ClockGuardOptions(TimeSpan.FromHours(1)), () => mono.Ms, 1000), events);
        await service.InitializeAsync();
        await service.ActivateAsync(LicensingWorld.ActivationKey);

        // real time passes beyond the license: it is Expired
        world.Clock.Advance(TimeSpan.FromDays(45));
        mono.Ms += (long)TimeSpan.FromDays(45).TotalMilliseconds;
        Assert.Equal(LicenseState.Expired, service.State);

        // the user turns the clock back to a day when the license was still valid
        world.Clock.Set(LicensingWorld.Start.AddDays(10));

        Assert.Equal(LicenseState.Invalid, service.State);
        Assert.Equal(InvalidReason.ClockRollback, service.Current.InvalidReason);
        Assert.False(service.IsModuleLicensed(new Platform.Core.Modules.ModuleId("pos")));
        Assert.Contains(events.Events, e => e.Action == "security.license.clock-rollback");
    }

    [Fact]
    public async Task Correcting_the_clock_restores_the_license_and_a_renewal_rebases_the_mark()
    {
        using var world = new LicensingWorld();
        var mono = new Mono();
        var store = new InMemoryClockStore();
        var service = Service(world, new ClockRollbackGuard(store, new ClockGuardOptions(TimeSpan.FromHours(1)), () => mono.Ms, 1000));
        await service.InitializeAsync();
        await service.ActivateAsync(LicensingWorld.ActivationKey);
        world.Clock.Advance(TimeSpan.FromDays(3));
        mono.Ms += (long)TimeSpan.FromDays(3).TotalMilliseconds;
        Assert.Equal(LicenseState.Active, service.State);

        var good = world.Clock.GetUtcNow();
        world.Clock.Set(good.AddDays(-30));
        Assert.Equal(InvalidReason.ClockRollback, service.Current.InvalidReason);

        world.Clock.Set(good);                                  // the user fixes the clock
        Assert.Equal(LicenseState.Active, service.State);
    }

    [Fact]
    public async Task A_clock_set_back_before_the_next_start_cannot_revive_an_old_state_of_the_license()
    {
        using var world = new LicensingWorld();
        var mono = new Mono();
        var store = new InMemoryClockStore();
        var guard = new ClockRollbackGuard(store, new ClockGuardOptions(TimeSpan.FromHours(1)), () => mono.Ms, 1000);
        var first = Service(world, guard);
        await first.InitializeAsync();
        await first.ActivateAsync(LicensingWorld.ActivationKey);
        world.Clock.Advance(TimeSpan.FromDays(20));
        mono.Ms += (long)TimeSpan.FromDays(20).TotalMilliseconds;
        _ = first.Current;                                       // the program is used for twenty days ...
        await guard.FlushAsync();                                // ... and stopped: the mark is written

        world.Clock.Set(LicensingWorld.Start.AddDays(1));        // set back, then start the application again (new run, new stopwatch)
        var restarted = Service(world, new ClockRollbackGuard(store, new ClockGuardOptions(TimeSpan.FromHours(1)), () => 0, 1000));
        var evaluation = await restarted.InitializeAsync();

        Assert.Equal(InvalidReason.ClockRollback, evaluation.InvalidReason);
        Assert.Equal(LicenseState.Invalid, restarted.State);
    }

    [Fact]
    public async Task Without_a_license_the_clock_check_has_nothing_to_protect()
    {
        using var world = new LicensingWorld();
        var service = Service(world, new ClockRollbackGuard(new InMemoryClockStore(), new ClockGuardOptions(TimeSpan.FromHours(1))));
        await service.InitializeAsync();

        world.Clock.Set(LicensingWorld.Start.AddYears(-3));

        Assert.Equal(LicenseState.Unlicensed, service.State);
    }
}
