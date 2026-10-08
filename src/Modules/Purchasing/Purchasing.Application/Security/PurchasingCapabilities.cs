using Platform.Application.Abstractions.Authorization;

namespace Purchasing.Application.Security;

/// <summary>The capabilities the Purchasing module's operations check (declared to the platform catalog by <see cref="PurchasingCapabilityProvider"/>).</summary>
public static class PurchasingCapabilities
{
    public const string Module = "purchasing";

    public const string EditOrder = "purchasing.order.create";
    public const string SubmitOrder = "purchasing.order.submit";
    public const string CancelOrder = "purchasing.order.cancel";
    public const string ReceiveOrder = "purchasing.order.receive";
    public const string ReturnGoods = "purchasing.return.create";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(EditOrder, Module, "Create and edit purchase orders", "Create purchase orders and change their lines while they are drafts.", LicenseRequirement.Module),
        new(SubmitOrder, Module, "Submit purchase orders", "Send a draft purchase order to the supplier.", LicenseRequirement.Module),
        new(CancelOrder, Module, "Cancel purchase orders", "Cancel a purchase order that has not been received, or close a partly received one short.", LicenseRequirement.Module),
        new(ReceiveOrder, Module, "Receive purchase orders", "Book the goods of a purchase order into stock.", LicenseRequirement.Module, IsSensitive: true),
        new(ReturnGoods, Module, "Return goods to suppliers", "Send received goods of a purchase order back to the supplier; they leave stock.", LicenseRequirement.Module, IsSensitive: true)
    ];
}

public sealed class PurchasingCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => PurchasingCapabilities.All;
}
