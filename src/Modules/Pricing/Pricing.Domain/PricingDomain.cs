using Platform.Core.Results;

namespace Pricing.Domain.ValueObjects
{
    public readonly record struct PriceListId(Guid Value)
    {
        public static PriceListId New() => new(Guid.NewGuid());
        public static PriceListId Empty => new(Guid.Empty);
        public override string ToString() => Value.ToString();
    }

    public readonly record struct PriceId(Guid Value)
    {
        public static PriceId New() => new(Guid.NewGuid());
        public static PriceId Empty => new(Guid.Empty);
        public override string ToString() => Value.ToString();
    }

    /// <summary>A non-negative price amount (rounded to 4 decimals). Currency handling is a later concern.</summary>
    public readonly record struct Money(decimal Amount)
    {
        public static Result<Money> Create(decimal amount)
            => amount < 0m
                ? Result.Failure<Money>(Error.Validation("Pricing.Price.NegativeAmount", "A price cannot be negative."))
                : Result.Success(new Money(decimal.Round(amount, 4, MidpointRounding.AwayFromZero)));
    }
}

namespace Pricing.Domain.Enums
{
    public enum PriceStatus
    {
        Active = 1,
        Inactive = 2
    }

    public enum PriceListStatus
    {
        Active = 1,
        Inactive = 2
    }
}

namespace Pricing.Domain.Entities
{
    using Pricing.Domain.Enums;
    using Pricing.Domain.ValueObjects;

    /// <summary>
    /// A named list of prices (aggregate root). Today there is a default list; the model leaves room for customer-specific or
    /// scheduled lists later (a list is selected by ID or code; nothing here knows about customers or promotions).
    /// </summary>
    public sealed class PriceList
    {
        private PriceList() { }

        public PriceListId Id { get; private set; }
        public string Code { get; private set; } = string.Empty;
        public string Name { get; private set; } = string.Empty;
        public bool IsDefault { get; private set; }
        public PriceListStatus Status { get; private set; }
        public DateTime CreatedAt { get; private set; }

        public static Result<PriceList> Create(string code, string name, bool isDefault)
        {
            if (string.IsNullOrWhiteSpace(code))
                return Result.Failure<PriceList>(Error.Validation("Pricing.PriceList.CodeRequired", "A price list code is required."));
            if (code.Trim().Length > 30)
                return Result.Failure<PriceList>(Error.Validation("Pricing.PriceList.CodeTooLong", "The price list code cannot exceed 30 characters."));
            if (string.IsNullOrWhiteSpace(name))
                return Result.Failure<PriceList>(Error.Validation("Pricing.PriceList.NameRequired", "A price list name is required."));
            if (name.Trim().Length > 200)
                return Result.Failure<PriceList>(Error.Validation("Pricing.PriceList.NameTooLong", "The price list name cannot exceed 200 characters."));

            return Result.Success(new PriceList
            {
                Id = PriceListId.New(),
                Code = code.Trim().ToUpperInvariant(),
                Name = name.Trim(),
                IsDefault = isDefault,
                Status = PriceListStatus.Active,
                CreatedAt = DateTime.UtcNow
            });
        }

        public Result MakeDefault()
        {
            if (Status != PriceListStatus.Active)
                return Result.Failure(Error.Conflict("Pricing.PriceList.Inactive", "An inactive price list cannot be the default."));

            IsDefault = true;
            return Result.Success();
        }

        public void ClearDefault() => IsDefault = false;

        public Result Deactivate()
        {
            if (Status == PriceListStatus.Inactive)
                return Result.Failure(Error.Conflict("Pricing.PriceList.AlreadyInactive", "The price list is already inactive."));
            if (IsDefault)
                return Result.Failure(Error.Conflict("Pricing.PriceList.DefaultCannotBeDeactivated", "The default price list cannot be deactivated. Make another list the default first."));

            Status = PriceListStatus.Inactive;
            return Result.Success();
        }
    }

    /// <summary>
    /// A price for a product in a price list, valid from a date (optionally until another) and from a minimum quantity.
    /// The product is referenced by its Catalog ID only. The price is a CURRENT-price rule: transaction modules (Sales/POS) snapshot
    /// the amount they used, so changing or deactivating a price never rewrites history.
    /// </summary>
    public sealed class Price
    {
        private Price() { }

        public PriceId Id { get; private set; }
        public PriceListId PriceListId { get; private set; }
        public Guid ProductId { get; private set; }
        public Money Amount { get; private set; }

        /// <summary>The price applies from this quantity upwards (1 = ordinary price; larger = quantity break).</summary>
        public decimal MinimumQuantity { get; private set; }

        public DateTime EffectiveFrom { get; private set; }
        public DateTime? EffectiveTo { get; private set; }
        public PriceStatus Status { get; private set; }
        public DateTime CreatedAt { get; private set; }
        public DateTime UpdatedAt { get; private set; }

        public static Result<Price> Create(
            PriceListId priceListId, Guid productId, decimal amount, decimal minimumQuantity, DateTime effectiveFrom, DateTime? effectiveTo)
        {
            var checks = Validate(priceListId, productId, amount, minimumQuantity, effectiveFrom, effectiveTo);
            if (checks.IsFailure) return Result.Failure<Price>(checks.Error);

            var now = DateTime.UtcNow;
            return Result.Success(new Price
            {
                Id = PriceId.New(),
                PriceListId = priceListId,
                ProductId = productId,
                Amount = new Money(decimal.Round(amount, 4, MidpointRounding.AwayFromZero)),
                MinimumQuantity = minimumQuantity,
                EffectiveFrom = effectiveFrom,
                EffectiveTo = effectiveTo,
                Status = PriceStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        public Result Update(decimal amount, decimal minimumQuantity, DateTime effectiveFrom, DateTime? effectiveTo)
        {
            if (Status != PriceStatus.Active)
                return Result.Failure(Error.Conflict("Pricing.Price.Inactive", "An inactive price cannot be changed. Reactivate it first."));

            var checks = Validate(PriceListId, ProductId, amount, minimumQuantity, effectiveFrom, effectiveTo);
            if (checks.IsFailure) return checks;

            Amount = new Money(decimal.Round(amount, 4, MidpointRounding.AwayFromZero));
            MinimumQuantity = minimumQuantity;
            EffectiveFrom = effectiveFrom;
            EffectiveTo = effectiveTo;
            UpdatedAt = DateTime.UtcNow;
            return Result.Success();
        }

        public Result Deactivate()
        {
            if (Status == PriceStatus.Inactive)
                return Result.Failure(Error.Conflict("Pricing.Price.AlreadyInactive", "The price is already inactive."));

            Status = PriceStatus.Inactive;
            UpdatedAt = DateTime.UtcNow;
            return Result.Success();
        }

        public Result Reactivate()
        {
            if (Status == PriceStatus.Active)
                return Result.Failure(Error.Conflict("Pricing.Price.AlreadyActive", "The price is already active."));

            Status = PriceStatus.Active;
            UpdatedAt = DateTime.UtcNow;
            return Result.Success();
        }

        /// <summary>Active, inside its validity period (From inclusive, To exclusive) and the quantity reaches the minimum.</summary>
        public bool AppliesAt(DateTime at, decimal quantity)
            => Status == PriceStatus.Active
               && at >= EffectiveFrom
               && (EffectiveTo is null || at < EffectiveTo.Value)
               && quantity >= MinimumQuantity;

        /// <summary>True when two prices for the same product/list/quantity-break have overlapping validity periods.</summary>
        public bool OverlapsWith(Price other)
        {
            if (other.Id == Id || other.ProductId != ProductId || other.PriceListId != PriceListId || other.MinimumQuantity != MinimumQuantity)
                return false;

            return EffectiveFrom < (other.EffectiveTo ?? DateTime.MaxValue) && other.EffectiveFrom < (EffectiveTo ?? DateTime.MaxValue);
        }

        private static Result Validate(PriceListId priceListId, Guid productId, decimal amount, decimal minimumQuantity, DateTime from, DateTime? to)
        {
            if (priceListId == PriceListId.Empty)
                return Result.Failure(Error.Validation("Pricing.Price.PriceListRequired", "A price list is required."));
            if (productId == Guid.Empty)
                return Result.Failure(Error.Validation("Pricing.Price.ProductRequired", "A product is required."));
            if (amount < 0m)
                return Result.Failure(Error.Validation("Pricing.Price.NegativeAmount", "A price cannot be negative."));
            if (minimumQuantity <= 0m)
                return Result.Failure(Error.Validation("Pricing.Price.InvalidMinimumQuantity", "The minimum quantity must be greater than zero."));
            if (to is not null && to.Value <= from)
                return Result.Failure(Error.Validation("Pricing.Price.InvalidPeriod", "The end of the validity period must be after its start."));

            return Result.Success();
        }
    }
}

namespace Pricing.Domain.Services
{
    using Pricing.Domain.Entities;

    /// <summary>
    /// The price selection rule (pure): among the prices that apply at the given time and quantity, the one with the HIGHEST minimum
    /// quantity wins (the best quantity break reached), then the latest start date.
    /// </summary>
    public static class PriceSelection
    {
        public static Price? Select(IEnumerable<Price> candidates, DateTime at, decimal quantity)
            => candidates
                .Where(p => p.AppliesAt(at, quantity))
                .OrderByDescending(p => p.MinimumQuantity)
                .ThenByDescending(p => p.EffectiveFrom)
                .FirstOrDefault();
    }
}
