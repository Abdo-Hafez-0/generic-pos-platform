using System.Globalization;
using Catalog.Contracts.Interfaces;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Pricing.Application.Abstractions;
using Pricing.Application.DTOs;
using Pricing.Application.Repositories;
using Pricing.Domain.Entities;
using Pricing.Domain.Enums;
using Pricing.Domain.Services;
using Pricing.Domain.ValueObjects;

// FIX-08a: tax rates live in Pricing (user decision): named rates with one default, and an optional rate per product.
// Prices INCLUDE tax; the till snapshots the rate of each line, so changing a rate never rewrites a past sale.

namespace Pricing.Application.Repositories
{
    public interface ITaxRateRepository
    {
        Task<TaxRate?> GetByIdAsync(TaxRateId id, CancellationToken cancellationToken = default);
        Task<TaxRate?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
        Task<TaxRate?> GetDefaultAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<TaxRate>> ListAsync(CancellationToken cancellationToken = default);
        Task AddAsync(TaxRate rate, CancellationToken cancellationToken = default);
    }

    public interface IProductTaxRateRepository
    {
        Task<ProductTaxRate?> GetAsync(Guid productId, CancellationToken cancellationToken = default);
        Task AddAsync(ProductTaxRate assignment, CancellationToken cancellationToken = default);
        void Remove(ProductTaxRate assignment);
    }
}

namespace Pricing.Application.DTOs
{
    public sealed record TaxRateDto(Guid TaxRateId, string Code, string Name, decimal Rate, bool IsDefault, TaxRateStatus Status);

    /// <summary>The tax of one product: its own choice (null = it uses the default) and the rate that applies now (null = no tax).</summary>
    public sealed record ProductTaxDto(Guid ProductId, TaxRateDto? Assigned, TaxRateDto? Effective);
}

namespace Pricing.Application.Commands
{
    using Pricing.Application.Queries;

    internal static class TaxAudit
    {
        public static string Percent(decimal rate) => (rate * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

        public static Task RecordAsync(IBusinessEventSink? sink, string action, Guid id, string summary, string? details = null)
            => sink.TryRecordAsync(BusinessEvent.Create("pricing", action, "tax-rate", id.ToString(), summary, details));
    }

    /// <summary>Creates a tax rate. The first rate ever created becomes the default automatically.</summary>
    public sealed record CreateTaxRateCommand(string Code, string Name, decimal Rate, bool MakeDefault = false);

    public sealed class CreateTaxRateCommandHandler(ITaxRateRepository rates, IPricingUnitOfWork unitOfWork, IAuthorizationService authorization, IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result<Guid>> HandleAsync(CreateTaxRateCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManageTaxRates, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

            var all = await rates.ListAsync(cancellationToken);
            var makeDefault = command.MakeDefault || all.Count == 0;
            var created = TaxRate.Create(command.Code, command.Name, command.Rate, makeDefault);
            if (created.IsFailure) return Result.Failure<Guid>(created.Error);

            if (all.Any(r => r.Code == created.Value.Code))
                return Result.Failure<Guid>(Error.Conflict("Pricing.TaxRate.DuplicateCode", $"A tax rate with code '{created.Value.Code}' already exists."));

            if (makeDefault)
                foreach (var other in all) other.ClearDefault();

            await rates.AddAsync(created.Value, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await TaxAudit.RecordAsync(businessEvents, "tax-rate.created", created.Value.Id.Value,
                $"Tax rate {created.Value.Code} ({created.Value.Name}) created at {TaxAudit.Percent(created.Value.Rate)}{(makeDefault ? ", the default" : string.Empty)}.");
            return Result.Success(created.Value.Id.Value);
        }
    }

    /// <summary>Changes a rate's name or percentage (a new VAT law). Past sales keep the rate they were sold at.</summary>
    public sealed record UpdateTaxRateCommand(Guid TaxRateId, string Name, decimal Rate);

    public sealed class UpdateTaxRateCommandHandler(ITaxRateRepository rates, IPricingUnitOfWork unitOfWork, IAuthorizationService authorization, IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result> HandleAsync(UpdateTaxRateCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManageTaxRates, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var rate = await rates.GetByIdAsync(new TaxRateId(command.TaxRateId), cancellationToken);
            if (rate is null)
                return Result.Failure(Error.NotFound("Pricing.TaxRate.NotFound", $"Tax rate '{command.TaxRateId}' was not found."));

            var before = TaxAudit.Percent(rate.Rate);
            var updated = rate.Update(command.Name, command.Rate);
            if (updated.IsFailure) return updated;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await TaxAudit.RecordAsync(businessEvents, "tax-rate.changed", rate.Id.Value, $"Tax rate {rate.Code} changed from {before} to {TaxAudit.Percent(rate.Rate)} ({rate.Name}).");
            return Result.Success();
        }
    }

    public sealed record SetDefaultTaxRateCommand(Guid TaxRateId);

    public sealed class SetDefaultTaxRateCommandHandler(ITaxRateRepository rates, IPricingUnitOfWork unitOfWork, IAuthorizationService authorization, IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result> HandleAsync(SetDefaultTaxRateCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManageTaxRates, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var all = await rates.ListAsync(cancellationToken);
            var target = all.FirstOrDefault(r => r.Id == new TaxRateId(command.TaxRateId));
            if (target is null)
                return Result.Failure(Error.NotFound("Pricing.TaxRate.NotFound", $"Tax rate '{command.TaxRateId}' was not found."));

            var made = target.MakeDefault();
            if (made.IsFailure) return made;

            foreach (var other in all.Where(r => r.Id != target.Id && r.IsDefault)) other.ClearDefault();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await TaxAudit.RecordAsync(businessEvents, "tax-rate.default-set", target.Id.Value, $"Tax rate {target.Code} ({TaxAudit.Percent(target.Rate)}) is now the default.");
            return Result.Success();
        }
    }

    public sealed record DeactivateTaxRateCommand(Guid TaxRateId);

    public sealed class DeactivateTaxRateCommandHandler(ITaxRateRepository rates, IPricingUnitOfWork unitOfWork, IAuthorizationService authorization, IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result> HandleAsync(DeactivateTaxRateCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManageTaxRates, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var rate = await rates.GetByIdAsync(new TaxRateId(command.TaxRateId), cancellationToken);
            if (rate is null)
                return Result.Failure(Error.NotFound("Pricing.TaxRate.NotFound", $"Tax rate '{command.TaxRateId}' was not found."));

            var result = rate.Deactivate();
            if (result.IsFailure) return result;

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await TaxAudit.RecordAsync(businessEvents, "tax-rate.deactivated", rate.Id.Value,
                $"Tax rate {rate.Code} ({TaxAudit.Percent(rate.Rate)}) deactivated; products that used it now use the default.");
            return Result.Success();
        }
    }

    /// <summary>Chooses the tax rate of a product (SKU or Catalog ID); a null rate = the product uses the default rate.</summary>
    public sealed record SetProductTaxRateCommand(string ProductCode, Guid? TaxRateId);

    public sealed class SetProductTaxRateCommandHandler(
        ITaxRateRepository rates,
        IProductTaxRateRepository assignments,
        IProductLookup productLookup,
        IPricingUnitOfWork unitOfWork,
        IAuthorizationService authorization,
        IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result> HandleAsync(SetProductTaxRateCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(Pricing.Application.Security.PricingCapabilities.ManageTaxRates, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var code = command.ProductCode?.Trim() ?? string.Empty;
            var product = Guid.TryParse(code, out var productId)
                ? await productLookup.FindByIdAsync(productId, cancellationToken)
                : await productLookup.FindBySkuAsync(code, cancellationToken);
            if (product is null)
                return Result.Failure(Error.NotFound("Pricing.ProductTaxRate.ProductNotFound", $"No product found for '{code}'."));

            TaxRate? rate = null;
            if (command.TaxRateId is { } id)
            {
                rate = await rates.GetByIdAsync(new TaxRateId(id), cancellationToken);
                if (rate is null)
                    return Result.Failure(Error.NotFound("Pricing.TaxRate.NotFound", $"Tax rate '{id}' was not found."));
                if (rate.Status != TaxRateStatus.Active)
                    return Result.Failure(Error.Conflict("Pricing.TaxRate.Inactive", "An inactive tax rate cannot be chosen for a product."));
            }

            var existing = await assignments.GetAsync(product.ProductId, cancellationToken);
            if (rate is null)
            {
                if (existing is null) return Result.Success();
                assignments.Remove(existing);
            }
            else if (existing is null)
            {
                var created = ProductTaxRate.Create(product.ProductId, rate.Id);
                if (created.IsFailure) return created;
                await assignments.AddAsync(created.Value, cancellationToken);
            }
            else
            {
                existing.Change(rate.Id);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await businessEvents.TryRecordAsync(BusinessEvent.Create("pricing", "product.tax-rate-set", "product", product.ProductId.ToString(),
                rate is null
                    ? $"Product {product.Sku} now uses the default tax rate."
                    : $"Product {product.Sku} now uses tax rate {rate.Code} ({TaxAudit.Percent(rate.Rate)})."));
            return Result.Success();
        }
    }
}

namespace Pricing.Application.Queries
{
    internal static class TaxMapping
    {
        public static TaxRateDto ToDto(this TaxRate r) => new(r.Id.Value, r.Code, r.Name, r.Rate, r.IsDefault, r.Status);
    }

    public sealed record ListTaxRatesQuery;

    public sealed class ListTaxRatesQueryHandler(ITaxRateRepository rates)
    {
        public async Task<IReadOnlyList<TaxRateDto>> HandleAsync(ListTaxRatesQuery query, CancellationToken cancellationToken = default)
            => (await rates.ListAsync(cancellationToken))
                .OrderByDescending(r => r.Status == TaxRateStatus.Active).ThenByDescending(r => r.IsDefault).ThenBy(r => r.Code)
                .Select(r => r.ToDto()).ToList();
    }

    /// <summary>"Which tax applies to this product now?" - the use case behind the ITaxRateResolver contract and the prices screen.</summary>
    public sealed record GetProductTaxQuery(Guid ProductId);

    public sealed class GetProductTaxQueryHandler(ITaxRateRepository rates, IProductTaxRateRepository assignments)
    {
        public async Task<ProductTaxDto> HandleAsync(GetProductTaxQuery query, CancellationToken cancellationToken = default)
        {
            var assignment = query.ProductId == Guid.Empty ? null : await assignments.GetAsync(query.ProductId, cancellationToken);
            var assigned = assignment is null ? null : await rates.GetByIdAsync(assignment.TaxRateId, cancellationToken);
            var effective = TaxSelection.Select(assigned, await rates.GetDefaultAsync(cancellationToken));
            return new ProductTaxDto(query.ProductId, assigned?.ToDto(), effective?.ToDto());
        }
    }
}
