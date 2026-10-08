using Platform.Application.Abstractions.Authorization;

namespace Pricing.Application.Security;

/// <summary>The capabilities the Pricing module's operations check (declared to the platform catalog by <see cref="PricingCapabilityProvider"/>).</summary>
public static class PricingCapabilities
{
    public const string Module = "pricing";

    public const string ManagePriceLists = "pricing.pricelist.manage";
    public const string ManagePrices = "pricing.price.manage";
    public const string ManageTaxRates = "pricing.tax.manage";   // FIX-08a

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(ManagePriceLists, Module, "Manage price lists", "Create price lists, choose the default and deactivate them.", LicenseRequirement.Module, IsSensitive: true),
        new(ManagePrices, Module, "Manage prices", "Create, change and deactivate prices.", LicenseRequirement.Module, IsSensitive: true),
        new(ManageTaxRates, Module, "Manage tax rates", "Create and change tax rates, choose the default and the rate of each product.", LicenseRequirement.Module, IsSensitive: true)
    ];
}

public sealed class PricingCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => PricingCapabilities.All;
}
