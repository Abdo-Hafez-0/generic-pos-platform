using System.Text.RegularExpressions;

namespace Platform.Application.Abstractions.Authorization;

/// <summary>Whether using a capability also needs the owning module to be licensed.</summary>
public enum LicenseRequirement
{
    /// <summary>
    /// Available in every license state. Used for operations that must keep working when a license is missing, expired or revoked:
    /// reading and exporting one's own data, backup and restore, user and license administration, auditing.
    /// </summary>
    None = 0,

    /// <summary>The module that owns the capability must be entitled by the current signed license (offline lease rules apply).</summary>
    Module = 1
}

/// <summary>
/// One thing a user can be allowed to do, declared by the module that owns the operation (architecture: "modules can register
/// their own permissions"). The <see cref="Code"/> is what roles store and what handlers check.
/// </summary>
/// <param name="Code">Lower-case dot-separated words, at least two, e.g. "pos.sale.create".</param>
/// <param name="Module">The owning module's ID, e.g. "pos". Used for license enforcement.</param>
/// <param name="DisplayName">Short human-readable name.</param>
/// <param name="Description">What holding the capability allows.</param>
/// <param name="License">Whether the module must be licensed to use the capability.</param>
/// <param name="IsSensitive">True for capabilities that move money or stock, change security or touch other people's accounts.</param>
public sealed record CapabilityDescriptor(
    string Code,
    string Module,
    string DisplayName,
    string Description,
    LicenseRequirement License = LicenseRequirement.Module,
    bool IsSensitive = false);

/// <summary>Implemented once per module (in its Application layer) to declare the capabilities its operations check.</summary>
public interface ICapabilityProvider
{
    IReadOnlyCollection<CapabilityDescriptor> GetCapabilities();
}

/// <summary>Every capability declared by the modules that are installed in this host.</summary>
public interface ICapabilityCatalog
{
    IReadOnlyCollection<CapabilityDescriptor> All { get; }

    /// <summary>Case-insensitive lookup; null for a capability nobody declared.</summary>
    CapabilityDescriptor? Find(string code);
}

public static partial class CapabilityCodes
{
    public const int MaxLength = 100;

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)+$")]
    private static partial Regex Pattern();

    /// <summary>The same format the Users module accepts for a stored permission code.</summary>
    public static bool IsValid(string? code)
        => !string.IsNullOrEmpty(code) && code.Length <= MaxLength && Pattern().IsMatch(code);
}

/// <summary>The in-memory catalog built from every registered <see cref="ICapabilityProvider"/>. Duplicate codes are a programming error.</summary>
public sealed class CapabilityCatalog : ICapabilityCatalog
{
    private readonly Dictionary<string, CapabilityDescriptor> _byCode = new(StringComparer.OrdinalIgnoreCase);

    public CapabilityCatalog(IEnumerable<ICapabilityProvider> providers)
    {
        foreach (var descriptor in providers.SelectMany(p => p.GetCapabilities()))
        {
            if (!CapabilityCodes.IsValid(descriptor.Code))
                throw new InvalidOperationException($"'{descriptor.Code}' is not a valid capability code (lower-case dot-separated words).");

            if (string.IsNullOrWhiteSpace(descriptor.Module))
                throw new InvalidOperationException($"Capability '{descriptor.Code}' does not name its module.");

            if (!_byCode.TryAdd(descriptor.Code, descriptor))
                throw new InvalidOperationException($"Capability '{descriptor.Code}' is declared more than once.");
        }

        All = _byCode.Values.OrderBy(d => d.Code, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyCollection<CapabilityDescriptor> All { get; }

    public CapabilityDescriptor? Find(string code)
        => string.IsNullOrEmpty(code) ? null : _byCode.GetValueOrDefault(code);
}
