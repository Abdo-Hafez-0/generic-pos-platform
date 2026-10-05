using Platform.Application.Abstractions.Authorization;

namespace Payments.Application.Security;

/// <summary>The capabilities the Payments module's operations check (declared to the platform catalog by <see cref="PaymentsCapabilityProvider"/>).</summary>
public static class PaymentsCapabilities
{
    public const string Module = "payments";

        public const string RecordPayment = "payments.payment.record";
        public const string VoidPayment = "payments.payment.void";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
            new(RecordPayment, Module, "Record payments", "Record a payment taken for a sale or other reference.", LicenseRequirement.Module, IsSensitive: true),
            new(VoidPayment, Module, "Void payments", "Void a recorded payment.", LicenseRequirement.Module, IsSensitive: true)
    ];
}

public sealed class PaymentsCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => PaymentsCapabilities.All;
}
