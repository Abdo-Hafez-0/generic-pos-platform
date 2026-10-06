using Platform.Application.Abstractions.Authorization;

namespace Reporting.Application.Security;

/// <summary>The capabilities the Reporting module's operations check (declared to the platform catalog by <see cref="ReportingCapabilityProvider"/>).</summary>
public static class ReportingCapabilities
{
    public const string Module = "reporting";

    public const string ViewReports = "reporting.view";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(ViewReports, Module, "View reports", "Open the business reports. Available in every license state: your data stays readable.", LicenseRequirement.None)
    ];
}

public sealed class ReportingCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => ReportingCapabilities.All;
}
