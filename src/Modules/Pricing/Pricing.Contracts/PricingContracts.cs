namespace Pricing.Contracts.Models
{
    /// <summary>The outcome of resolving the price of a product. Found = false means "no applicable price" (the caller falls back).</summary>
    public sealed record PriceResolutionResult(bool Found, decimal Amount, Guid PriceId, Guid PriceListId, string PriceListCode)
    {
        public static PriceResolutionResult None { get; } = new(false, 0m, Guid.Empty, Guid.Empty, string.Empty);
    }
}

namespace Pricing.Contracts.Interfaces
{
    using Pricing.Contracts.Models;

    /// <summary>
    /// Lets other modules (POS ...) ask "what is the price of this product right now?" without knowing how prices are stored or
    /// selected. The caller is responsible for snapshotting the returned amount into its own transaction record.
    /// Implemented by Pricing.Infrastructure.Services.PriceResolver. Fully offline.
    /// </summary>
    public interface IPriceResolver
    {
        /// <param name="productId">Catalog product ID.</param>
        /// <param name="quantity">The quantity being priced (quantity breaks apply).</param>
        /// <param name="at">The moment to price at (UTC). Defaults to now.</param>
        /// <param name="priceListId">A specific price list; null = the default price list.</param>
        Task<PriceResolutionResult> ResolveAsync(
            Guid productId, decimal quantity = 1m, DateTime? at = null, Guid? priceListId = null, CancellationToken cancellationToken = default);
    }
}
