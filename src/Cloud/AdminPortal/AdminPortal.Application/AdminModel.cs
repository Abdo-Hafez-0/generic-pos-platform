using System.Security.Cryptography;
using System.Text;

namespace AdminPortal.Application;

/// <summary>The authenticated vendor administrator behind a request. Passed explicitly; never ambient state.</summary>
public sealed record AdminActor(string Name);

// ---- Customers ---------------------------------------------------------------------------------------------------

/// <summary>A vendor-side customer (a business that buys licenses). Unrelated to the desktop Customers module.</summary>
public sealed class Customer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record CustomerPage(IReadOnlyList<Customer> Items, int Total);

public interface ICustomerRepository
{
    Task AddAsync(Customer customer, CancellationToken cancellationToken = default);

    Task SaveAsync(Customer customer, CancellationToken cancellationToken = default);

    Task<Customer?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive name check, optionally ignoring one customer (for renames).</summary>
    Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken = default);

    Task<CustomerPage> ListAsync(string? search, bool includeInactive, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<int> CountAsync(bool? isActive, CancellationToken cancellationToken = default);
}

// ---- Module registry ---------------------------------------------------------------------------------------------

public enum ModuleCategory
{
    Standard = 1,
    Optional = 2
}

/// <summary>A module the vendor sells/ships. The registry is the authority for which module IDs a license may entitle.</summary>
public sealed class RegisteredModule
{
    public string ModuleId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public ModuleCategory Category { get; set; } = ModuleCategory.Standard;

    /// <summary>Retired modules stay on record (existing licenses and packages are untouched) but get no new licenses or packages.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public interface IModuleRegistryRepository
{
    Task AddAsync(RegisteredModule module, CancellationToken cancellationToken = default);

    Task SaveAsync(RegisteredModule module, CancellationToken cancellationToken = default);

    Task<RegisteredModule?> FindAsync(string moduleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegisteredModule>> FindManyAsync(IEnumerable<string> moduleIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegisteredModule>> ListAsync(bool includeRetired, CancellationToken cancellationToken = default);
}

// ---- Administrative audit ----------------------------------------------------------------------------------------

/// <summary>One recorded administrative action. Append-only; never contains secrets.</summary>
public sealed class AdminAuditEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Summary { get; set; } = "";
}

public sealed record AuditFilter(string? Actor = null, string? Action = null, string? EntityType = null, string? EntityId = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null);

public sealed record AuditPage(IReadOnlyList<AdminAuditEntry> Items, int Total);

/// <summary>Append-only: there is deliberately no update or delete.</summary>
public interface IAdminAuditLog
{
    Task AppendAsync(AdminAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<AuditPage> QueryAsync(AuditFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);
}

/// <summary>Writes audit entries for administrative actions.</summary>
public sealed class AdminAuditRecorder(IAdminAuditLog log, TimeProvider timeProvider)
{
    public Task RecordAsync(AdminActor actor, string action, string entityType, string entityId, string summary, CancellationToken cancellationToken = default)
        => log.AppendAsync(new AdminAuditEntry
        {
            Id = Guid.NewGuid(),
            At = timeProvider.GetUtcNow(),
            Actor = actor.Name,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary
        }, cancellationToken);
}

// ---- Administrator authentication --------------------------------------------------------------------------------

/// <summary>A configured administrator credential: a name and the SHA-256 (hex) of the API key. The key itself is never stored.</summary>
public sealed record AdminKeyEntry(string Name, string Sha256);

public static class AdminKeys
{
    public const string Prefix = "gpa_";

    public static string Generate()
        => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()))).ToLowerInvariant();
}

/// <summary>
/// Minimum administration authentication for Stage 9: static API keys held as hashes in configuration. This is NOT the
/// desktop Users module and not a general identity system. Comparison is constant-time; no configured key = nobody gets in.
/// </summary>
public sealed class AdminKeyAuthenticator
{
    private readonly (string Name, byte[] Hash)[] _keys;

    public AdminKeyAuthenticator(IEnumerable<AdminKeyEntry> entries)
    {
        _keys = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.Sha256 is { Length: 64 } && e.Sha256.All(Uri.IsHexDigit))
            .Select(e => (e.Name.Trim(), Convert.FromHexString(e.Sha256)))
            .ToArray();
    }

    public int KeyCount => _keys.Length;

    public AdminActor? Authenticate(string? presentedKey)
    {
        if (string.IsNullOrWhiteSpace(presentedKey) || _keys.Length == 0)
            return null;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey.Trim()));

        string? match = null;
        foreach (var (name, expected) in _keys)
            if (CryptographicOperations.FixedTimeEquals(hash, expected))
                match = name;

        return match is null ? null : new AdminActor(match);
    }
}
