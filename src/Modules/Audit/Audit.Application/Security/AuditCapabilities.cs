using Platform.Application.Abstractions.Authorization;

namespace Audit.Application.Security;

/// <summary>The capabilities the Audit module's operations check (declared to the platform catalog by <see cref="AuditCapabilityProvider"/>).</summary>
public static class AuditCapabilities
{
    public const string Module = "audit";

    public const string ViewAudit = "audit.view";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(ViewAudit, Module, "View the audit trail", "Read the audit log, including security events.", LicenseRequirement.None, IsSensitive: true)
    ];
}

public sealed class AuditCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => AuditCapabilities.All;
}
