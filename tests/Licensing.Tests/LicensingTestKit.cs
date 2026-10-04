using Client.Licensing.Application;
using Client.Licensing.Domain;
using Client.Licensing.Infrastructure;
using LicenseServer.Application;
using LicenseServer.Infrastructure;
using Licensing.Contracts;

namespace Licensing.Tests;

/// <summary>Controllable clock so every date/lease/grace boundary is deterministic.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset value) => _now = value;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class InMemoryIdentityStore : IInstallationIdentityStore
{
    public InstallationIdentity? Stored { get; set; }
    public int Saves { get; private set; }
    public Task<InstallationIdentity?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stored);
    public Task SaveAsync(InstallationIdentity identity, CancellationToken cancellationToken = default)
    {
        Stored = identity; Saves++;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryLicenseStore : ILicenseStore
{
    public SignedLicense? Stored { get; set; }
    public bool Corrupt { get; set; }
    public Task<StoredLicense?> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<StoredLicense?>(Corrupt ? new StoredLicense(null) : Stored is null ? null : new StoredLicense(Stored));
    public Task SaveAsync(SignedLicense license, CancellationToken cancellationToken = default)
    {
        Stored = license;
        return Task.CompletedTask;
    }
}

/// <summary>Calls the license server logic in-process (no HTTP). Optional hook can tamper with responses.</summary>
public sealed class InProcessLicenseClient(LicenseIssuanceService server) : ILicenseClient
{
    public Func<SignedLicense, SignedLicense>? TamperLicense { get; set; }
    public int Calls { get; private set; }

    public async Task<ActivationResponse> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        var r = await server.ActivateAsync(request, cancellationToken);
        return r.IsSuccess && TamperLicense is not null ? r with { License = TamperLicense(r.License!) } : r;
    }

    public async Task<RenewalResponse> RenewAsync(RenewalRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        var r = await server.RenewAsync(request, cancellationToken);
        return r.IsSuccess && TamperLicense is not null ? r with { License = TamperLicense(r.License!) } : r;
    }
}

/// <summary>Simulates "Internet OFF / server unavailable".</summary>
public sealed class UnreachableLicenseClient : ILicenseClient
{
    public int Calls { get; private set; }
    public Task<ActivationResponse> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    { Calls++; throw new HttpRequestException("network down"); }
    public Task<RenewalResponse> RenewAsync(RenewalRequest request, CancellationToken cancellationToken = default)
    { Calls++; throw new HttpRequestException("network down"); }
}

/// <summary>
/// A complete in-memory licensing world: server (own key), client (trusting that key), stores and a manual clock.
/// No file system, no network, no real keys. Keys are generated per instance and never persisted.
/// </summary>
public sealed class LicensingWorld : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    public const string ProductId = "genericpos";
    public const string ActivationKey = "TEST-KEY-1";

    public ManualTimeProvider Clock { get; } = new(Start);
    public EcdsaLicenseSigner Signer { get; } = EcdsaLicenseSigner.GenerateEphemeral("test-key-1");
    public InMemoryLicenseRepository Repository { get; } = new();
    public LicenseServerOptions ServerOptions { get; } = new(TimeSpan.FromDays(30), TimeSpan.FromDays(7), "Test Issuer");
    public LicenseIssuanceService Server { get; }
    public InMemoryIdentityStore IdentityStore { get; } = new();
    public InMemoryLicenseStore LicenseStore { get; } = new();
    public InProcessLicenseClient Client { get; }
    public LicenseRecord Record { get; }

    public LicensingWorld(
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        string[]? modules = null,
        string[]? features = null)
    {
        Server = new LicenseIssuanceService(Repository, Signer, Clock, ServerOptions);
        Client = new InProcessLicenseClient(Server);
        Record = new LicenseRecord
        {
            LicenseId = Guid.NewGuid(),
            CustomerId = "customer-1",
            ActivationKey = ActivationKey,
            ProductId = ProductId,
            ValidFrom = validFrom ?? Start.AddDays(-1),
            ValidUntil = validUntil ?? Start.AddYears(1),
            Modules = modules ?? ["catalog", "inventory", "sales", "pos"],
            Features = features ?? ["advancedreports"]
        };
        Repository.AddAsync(Record).GetAwaiter().GetResult();
    }

    public EcdsaLicenseVerifier NewVerifier(string? publicKey = null, string keyId = "test-key-1")
        => new([new TrustedLicenseKey(keyId, publicKey ?? Signer.ExportPublicKey())]);

    /// <summary>A client-side service instance (simulates one application run). Stores are shared, so "restarts" see the same data.</summary>
    public LicenseService NewClientService(
        ILicenseClient? client = null,
        ILicenseVerifier? verifier = null,
        LicensePolicy? policy = null)
        => new(
            new InstallationIdentityService(IdentityStore, Clock),
            LicenseStore,
            verifier ?? NewVerifier(),
            client ?? Client,
            Clock,
            new LicensingOptions(ProductId),
            policy ?? new LicensePolicy());

    public void Dispose() => Signer.Dispose();
}
