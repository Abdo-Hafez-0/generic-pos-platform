using System.Globalization;
using System.Text;
using Catalog.Contracts.Interfaces;
using Fiscal.Application.Abstractions;
using Fiscal.Application.Security;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Pricing.Contracts.Interfaces;

namespace Fiscal.Application.Repositories
{
    /// <summary>MISS-11b: the authority's codes the shop keeps for its products, units and tax rates.</summary>
    public interface IFiscalCodeRepository
    {
        Task<IReadOnlyList<FiscalProductCode>> GetProductCodesAsync(IReadOnlyCollection<Guid>? productIds = null, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<FiscalUnitCode>> GetUnitCodesAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<FiscalTaxCode>> GetTaxCodesAsync(CancellationToken cancellationToken = default);
        Task AddAsync(FiscalProductCode code, CancellationToken cancellationToken = default);
        Task AddAsync(FiscalUnitCode code, CancellationToken cancellationToken = default);
        Task AddAsync(FiscalTaxCode code, CancellationToken cancellationToken = default);
        void Remove(FiscalProductCode code);
        void Remove(FiscalUnitCode code);
    }
}

namespace Fiscal.Application.DTOs
{
    /// <summary>Where a product's code comes from: saved by the shop, its GS1 barcode, or missing.</summary>
    public enum FiscalCodeSource
    {
        Missing = 0,
        Saved = 1,
        Barcode = 2,
    }

    public sealed record FiscalProductCodeRow(Guid ProductId, string Sku, string Name, Guid UnitId, string UnitName, string? Barcode, FiscalItemCodeType? Type,
        string? Code, FiscalCodeSource Source);

    public sealed record FiscalUnitCodeRow(Guid UnitId, string Name, string Abbreviation, string? Code, bool IsUsed);

    public sealed record FiscalTaxCodeRow(Guid TaxRateId, string Code, string Name, decimal Rate, bool IsDefault, string? EgyptTaxType, string? EgyptSubType,
        string? SaudiCategory, string? SaudiExemptionCode, string? SaudiExemptionReason, bool IsComplete);

    /// <summary>
    /// MISS-11b: every product, unit and tax rate with its code for the chosen country, and what is missing. Egypt needs all three; Saudi
    /// Arabia needs the tax rates' categories only. <see cref="CatalogInstalled"/> / <see cref="PricingInstalled"/>: without those modules
    /// there is nothing to code.
    /// </summary>
    public sealed record FiscalCodesDto(FiscalCountry Country, IReadOnlyList<FiscalProductCodeRow> Products, IReadOnlyList<FiscalUnitCodeRow> Units,
        IReadOnlyList<FiscalTaxCodeRow> TaxRates, bool CatalogInstalled, bool PricingInstalled)
    {
        public bool NeedsProductAndUnitCodes => Country == FiscalCountry.Egypt;
        public int MissingProducts => NeedsProductAndUnitCodes ? Products.Count(p => p.Source == FiscalCodeSource.Missing) : 0;
        public int MissingUnits => NeedsProductAndUnitCodes ? Units.Count(u => u.IsUsed && u.Code is null) : 0;
        public int MissingTaxRates => Country == FiscalCountry.None ? 0 : TaxRates.Count(t => !t.IsComplete);
        public int MissingTotal => MissingProducts + MissingUnits + MissingTaxRates;
    }
}

namespace Fiscal.Application.Abstractions
{
    using Fiscal.Application.DTOs;
    using Fiscal.Application.Repositories;

    /// <summary>
    /// MISS-11b: the codes of one sale line as the country part needs them: the product's item type and code (Egypt), its unit's code, and its
    /// tax rate's classification. Null where the shop has not set it (the part refuses to build, naming what is missing).
    /// </summary>
    public sealed record FiscalLineCodes(FiscalItemCodeType? ItemType, string? ItemCode, string? UnitCode, string? EgyptTaxType, string? EgyptSubType,
        string? SaudiCategory, string? SaudiExemptionCode, string? SaudiExemptionReason);

    /// <summary>
    /// MISS-11b: resolves the authority's codes - from what the shop saved, else (products) a valid GS1 barcode (owner's decision: used
    /// automatically). Reads the catalogue and the tax rates through their contracts; both modules are optional.
    /// </summary>
    public sealed class FiscalCodeBook(IFiscalCodeRepository codes, ICatalogListing? catalog = null, ITaxRateListing? taxRates = null,
        ITaxRateResolver? taxResolver = null)
    {
        public async Task<FiscalCodesDto> OverviewAsync(FiscalCountry country, CancellationToken cancellationToken = default)
        {
            var products = catalog is null ? [] : await catalog.ListProductsAsync(activeOnly: true, cancellationToken);
            var units = catalog is null ? [] : await catalog.ListUnitsAsync(cancellationToken);
            var rates = taxRates is null ? [] : await taxRates.ListAsync(cancellationToken);
            var productCodes = (await codes.GetProductCodesAsync(null, cancellationToken)).ToDictionary(c => c.ProductId);
            var unitCodes = (await codes.GetUnitCodesAsync(cancellationToken)).ToDictionary(c => c.UnitId);
            var taxCodes = (await codes.GetTaxCodesAsync(cancellationToken)).ToDictionary(c => c.TaxRateId);
            var usedUnits = products.Select(p => p.UnitId).ToHashSet();

            return new FiscalCodesDto(country,
                products.Select(p => ProductRow(p.ProductId, p.Sku, p.Name, p.UnitId, p.UnitName, p.Barcodes, productCodes.GetValueOrDefault(p.ProductId))).ToList(),
                units.Where(u => u.IsActive || usedUnits.Contains(u.UnitId))
                    .Select(u => new FiscalUnitCodeRow(u.UnitId, u.Name, u.Abbreviation, unitCodes.GetValueOrDefault(u.UnitId)?.Code, usedUnits.Contains(u.UnitId))).ToList(),
                rates.Where(r => r.IsActive).Select(r =>
                {
                    var t = taxCodes.GetValueOrDefault(r.TaxRateId);
                    return new FiscalTaxCodeRow(r.TaxRateId, r.Code, r.Name, r.Rate, r.IsDefault, t?.EgyptTaxType, t?.EgyptSubType, t?.SaudiCategory,
                        t?.SaudiExemptionCode, t?.SaudiExemptionReason, t?.IsCompleteFor(country) ?? country == FiscalCountry.None);
                }).ToList(),
                catalog is not null, taxRates is not null);
        }

        /// <summary>The codes of the products of a sale or a return, by product id (what is not set stays null).</summary>
        public async Task<IReadOnlyDictionary<Guid, FiscalLineCodes>> ForProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken = default)
        {
            if (productIds.Count == 0) return new Dictionary<Guid, FiscalLineCodes>();
            var wanted = productIds.ToHashSet();
            var products = catalog is null ? [] : (await catalog.ListProductsAsync(activeOnly: false, cancellationToken)).Where(p => wanted.Contains(p.ProductId)).ToList();
            var productCodes = (await codes.GetProductCodesAsync(productIds, cancellationToken)).ToDictionary(c => c.ProductId);
            var unitCodes = (await codes.GetUnitCodesAsync(cancellationToken)).ToDictionary(c => c.UnitId);
            var taxCodes = (await codes.GetTaxCodesAsync(cancellationToken)).ToDictionary(c => c.TaxRateId);

            var result = new Dictionary<Guid, FiscalLineCodes>();
            foreach (var id in wanted)
            {
                var product = products.FirstOrDefault(p => p.ProductId == id);
                var row = ProductRow(id, product?.Sku ?? string.Empty, product?.Name ?? string.Empty, product?.UnitId ?? Guid.Empty, product?.UnitName ?? string.Empty,
                    product?.Barcodes ?? [], productCodes.GetValueOrDefault(id));
                FiscalTaxCode? tax = null;
                if (taxResolver is not null && await taxResolver.ResolveAsync(id, cancellationToken) is { Found: true } rate)
                    tax = taxCodes.GetValueOrDefault(rate.TaxRateId);
                result[id] = new FiscalLineCodes(row.Type, row.Code, product is null ? null : unitCodes.GetValueOrDefault(product.UnitId)?.Code,
                    tax?.EgyptTaxType, tax?.EgyptSubType, tax?.SaudiCategory, tax?.SaudiExemptionCode, tax?.SaudiExemptionReason);
            }

            return result;
        }

        private static FiscalProductCodeRow ProductRow(Guid productId, string sku, string name, Guid unitId, string unitName, IReadOnlyList<string> barcodes,
            FiscalProductCode? saved)
        {
            var gs1 = barcodes.FirstOrDefault(Gs1.IsValid);
            var barcode = gs1 ?? barcodes.FirstOrDefault();
            return saved is not null
                ? new FiscalProductCodeRow(productId, sku, name, unitId, unitName, barcode, saved.Type, saved.Code, FiscalCodeSource.Saved)
                : gs1 is not null
                    ? new FiscalProductCodeRow(productId, sku, name, unitId, unitName, barcode, FiscalItemCodeType.GS1, gs1, FiscalCodeSource.Barcode)
                    : new FiscalProductCodeRow(productId, sku, name, unitId, unitName, barcode, null, null, FiscalCodeSource.Missing);
        }
    }
}

namespace Fiscal.Application.Queries
{
    using Fiscal.Application.DTOs;
    using Fiscal.Application.Repositories;

    public sealed record GetFiscalCodesQuery;

    /// <summary>MISS-11b: the Codes box - every product, unit and tax rate with its code and what is missing (fiscal.manage).</summary>
    public sealed class GetFiscalCodesQueryHandler(IFiscalSetupRepository setup, FiscalCodeBook codeBook, IAuthorizationService authorization)
    {
        public async Task<Result<FiscalCodesDto>> HandleAsync(GetFiscalCodesQuery query, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<FiscalCodesDto>(allowed.Error);

            var settings = await setup.GetSettingsAsync(cancellationToken);
            return Result.Success(await codeBook.OverviewAsync(settings?.Country ?? FiscalCountry.None, cancellationToken));
        }
    }

    public sealed record ExportEgsCodesQuery;

    /// <summary>
    /// MISS-11b (owner's decision: suggest and export): the products' EGS codes as a CSV file to register them on the ETA portal in one upload
    /// - code type, item code, names, active-from date; the parent GPC code (the GS1 product classification) is for the shop to fill on the
    /// portal's template *(to confirm: the columns of the portal's current upload template)*.
    /// </summary>
    public sealed class ExportEgsCodesQueryHandler(IFiscalSetupRepository setup, FiscalCodeBook codeBook, IAuthorizationService authorization,
        TimeProvider? timeProvider = null)
    {
        public async Task<Result<string>> HandleAsync(ExportEgsCodesQuery query, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<string>(allowed.Error);

            var settings = await setup.GetSettingsAsync(cancellationToken);
            var overview = await codeBook.OverviewAsync(settings?.Country ?? FiscalCountry.None, cancellationToken);
            var today = (timeProvider ?? TimeProvider.System).GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var csv = new StringBuilder();
            csv.AppendLine("Code Type,Parent Code,Item Code,Code Name,Code Name Arabic,Description,Description Arabic,Active From,Active To,Request Reason");
            foreach (var p in overview.Products.Where(p => p.Type == FiscalItemCodeType.EGS && p.Code is not null))
                csv.AppendLine(string.Join(",", "EGS", "", Csv(p.Code!), Csv(p.Name), "", Csv(p.Name), "", today, "", Csv("Product " + p.Sku)));
            return Result.Success(csv.ToString());
        }

        private static string Csv(string value) => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

namespace Fiscal.Application.Commands
{
    using Fiscal.Application.Repositories;
    using Fiscal.Application.Queries;
    using Platform.Application.Abstractions.Auditing;

    /// <summary>MISS-11b: a product's code as entered (a null or empty code removes the saved one: the barcode is used again, or it is missing).</summary>
    public sealed record FiscalProductCodeChange(Guid ProductId, FiscalItemCodeType Type, string? Code);

    public sealed record FiscalUnitCodeChange(Guid UnitId, string? Code);

    public sealed record FiscalTaxCodeChange(Guid TaxRateId, string? EgyptTaxType, string? EgyptSubType, string? SaudiCategory, string? SaudiExemptionCode,
        string? SaudiExemptionReason);

    /// <summary>MISS-11b: the changes made in the Codes box, saved together.</summary>
    public sealed record SaveFiscalCodesCommand(IReadOnlyList<FiscalProductCodeChange>? Products = null, IReadOnlyList<FiscalUnitCodeChange>? Units = null,
        IReadOnlyList<FiscalTaxCodeChange>? TaxRates = null);

    /// <summary>MISS-11b: saves codes (fiscal.manage, audited). All or nothing: a code in the wrong form refuses the save, naming it.</summary>
    public sealed class SaveFiscalCodesCommandHandler(IFiscalCodeRepository codes, IFiscalUnitOfWork unitOfWork, IAuthorizationService authorization,
        ICurrentUser? currentUser = null, TimeProvider? timeProvider = null, IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result> HandleAsync(SaveFiscalCodesCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var by = currentUser is { IsAuthenticated: true } me ? me.UserName : null;
            var products = (await codes.GetProductCodesAsync(null, cancellationToken)).ToDictionary(c => c.ProductId);
            var units = (await codes.GetUnitCodesAsync(cancellationToken)).ToDictionary(c => c.UnitId);
            var taxes = (await codes.GetTaxCodesAsync(cancellationToken)).ToDictionary(c => c.TaxRateId);

            foreach (var change in command.Products ?? [])
            {
                var existing = products.GetValueOrDefault(change.ProductId);
                if (string.IsNullOrWhiteSpace(change.Code))
                {
                    if (existing is not null) codes.Remove(existing);
                    continue;
                }

                var set = existing is null
                    ? FiscalProductCode.Create(change.ProductId, change.Type, change.Code, by, now) is var made && made.IsSuccess
                        ? await AddAsync(made.Value) : Result.Failure(made.Error)
                    : existing.Change(change.Type, change.Code, by, now);
                if (set.IsFailure) return set;
            }

            foreach (var change in command.Units ?? [])
            {
                var existing = units.GetValueOrDefault(change.UnitId);
                if (string.IsNullOrWhiteSpace(change.Code))
                {
                    if (existing is not null) codes.Remove(existing);
                    continue;
                }

                if (existing is null)
                {
                    var made = FiscalUnitCode.Create(change.UnitId, change.Code, now);
                    if (made.IsFailure) return made;
                    await codes.AddAsync(made.Value, cancellationToken);
                }
                else if (existing.Change(change.Code, now) is { IsFailure: true } failed) return failed;
            }

            foreach (var change in command.TaxRates ?? [])
            {
                var code = taxes.GetValueOrDefault(change.TaxRateId);
                var isNew = code is null;
                code ??= FiscalTaxCode.For(change.TaxRateId);
                if (code.ChangeEgypt(change.EgyptTaxType, change.EgyptSubType, now) is { IsFailure: true } egypt) return egypt;
                if (code.ChangeSaudi(change.SaudiCategory, change.SaudiExemptionCode, change.SaudiExemptionReason, now) is { IsFailure: true } saudi) return saudi;
                if (isNew) await codes.AddAsync(code, cancellationToken);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await businessEvents.TryRecordAsync(BusinessEvent.Create(FiscalCapabilities.Module, "fiscal.codes-saved", "fiscal-codes", "shop",
                $"Tax authority codes saved: {command.Products?.Count ?? 0} product(s), {command.Units?.Count ?? 0} unit(s), {command.TaxRates?.Count ?? 0} tax rate(s)."));
            return Result.Success();

            async Task<Result> AddAsync(FiscalProductCode made)
            {
                await codes.AddAsync(made, cancellationToken);
                return Result.Success();
            }
        }
    }
}
