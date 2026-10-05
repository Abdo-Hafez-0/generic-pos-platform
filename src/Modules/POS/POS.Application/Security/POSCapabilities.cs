using Platform.Application.Abstractions.Authorization;

namespace POS.Application.Security;

/// <summary>The capabilities the POS module's operations check (declared to the platform catalog by <see cref="POSCapabilityProvider"/>).</summary>
public static class POSCapabilities
{
    public const string Module = "pos";

        public const string ManageSession = "pos.session.manage";
        public const string CreateSale = "pos.sale.create";
        public const string ReprintReceipt = "pos.receipt.reprint";
        public const string OpenDrawer = "pos.drawer.open";
        public const string PrintLabel = "pos.label.print";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
            new(ManageSession, Module, "Open and close POS sessions", "Open a till session for a warehouse and close it.", LicenseRequirement.Module),
            new(CreateSale, Module, "Sell", "Start a cart, add, change and remove items, read the scale and check out.", LicenseRequirement.Module, IsSensitive: true),
            new(ReprintReceipt, Module, "Reprint receipts", "Print the receipt of a completed sale again.", LicenseRequirement.Module),
            new(OpenDrawer, Module, "Open the cash drawer", "Open the cash drawer without a sale.", LicenseRequirement.Module, IsSensitive: true),
            new(PrintLabel, Module, "Print product labels", "Print shelf or product labels.", LicenseRequirement.Module)
    ];
}

public sealed class POSCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => POSCapabilities.All;
}
