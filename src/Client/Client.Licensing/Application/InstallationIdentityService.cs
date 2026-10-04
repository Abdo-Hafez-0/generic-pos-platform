using Client.Licensing.Domain;

namespace Client.Licensing.Application;

/// <summary>Returns the stable installation identity, creating and persisting it exactly once.</summary>
public sealed class InstallationIdentityService(IInstallationIdentityStore store, TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InstallationIdentity? _cached;

    public async Task<InstallationIdentity> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null) return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null) return _cached;

            var existing = await store.LoadAsync(cancellationToken);
            if (existing is { IsValid: true })
                return _cached = existing;

            var created = InstallationIdentity.CreateNew(timeProvider.GetUtcNow());
            await store.SaveAsync(created, cancellationToken);
            return _cached = created;
        }
        finally
        {
            _gate.Release();
        }
    }
}
