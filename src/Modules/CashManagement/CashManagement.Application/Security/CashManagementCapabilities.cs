using Platform.Application.Abstractions.Authorization;

namespace CashManagement.Application.Security;

/// <summary>The capabilities the CashManagement module's operations check (declared to the platform catalog by <see cref="CashManagementCapabilityProvider"/>).</summary>
public static class CashManagementCapabilities
{
    public const string Module = "cash-management";

    public const string ManageSessions = "cash.session.manage";
    public const string RecordMovement = "cash.movement.record";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(ManageSessions, Module, "Open and close cash sessions", "Open a drawer session and close it with a count.", LicenseRequirement.Module, IsSensitive: true),
        new(RecordMovement, Module, "Record cash movements", "Record cash paid in or out of a drawer.", LicenseRequirement.Module, IsSensitive: true)
    ];
}

public sealed class CashManagementCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => CashManagementCapabilities.All;
}
