using Pricing.Domain.Entities;
using Pricing.Domain.Enums;
using Pricing.Domain.ValueObjects;

namespace Pricing.Application.Abstractions
{
    /// <summary>Saves changes to the Pricing module's own persistence (PricingDbContext).</summary>
    public interface IPricingUnitOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}

namespace Pricing.Application.Repositories
{
    public interface IPriceListRepository
    {
        Task<PriceList?> GetByIdAsync(PriceListId id, CancellationToken cancellationToken = default);
        Task<PriceList?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
        Task<PriceList?> GetDefaultAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<PriceList>> ListAsync(CancellationToken cancellationToken = default);
        Task AddAsync(PriceList list, CancellationToken cancellationToken = default);
    }

    public interface IPriceRepository
    {
        Task<Price?> GetByIdAsync(PriceId id, CancellationToken cancellationToken = default);
        Task AddAsync(Price price, CancellationToken cancellationToken = default);

        /// <summary>All prices (any status) of a product, optionally limited to one price list.</summary>
        Task<IReadOnlyList<Price>> ListForProductAsync(Guid productId, PriceListId? priceListId, CancellationToken cancellationToken = default);

        /// <summary>Active prices of a product in one price list (candidates for price resolution).</summary>
        Task<IReadOnlyList<Price>> ListActiveForProductAsync(Guid productId, PriceListId priceListId, CancellationToken cancellationToken = default);
    }
}

namespace Pricing.Application.DTOs
{
    public sealed record PriceListDto(Guid PriceListId, string Code, string Name, bool IsDefault, PriceListStatus Status);

    public sealed record PriceDto(
        Guid PriceId, Guid PriceListId, Guid ProductId, decimal Amount, decimal MinimumQuantity,
        DateTime EffectiveFrom, DateTime? EffectiveTo, PriceStatus Status);
}
