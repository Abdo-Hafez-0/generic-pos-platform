using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using Fiscal.Application.Commands;
using Fiscal.Application.DTOs;
using Fiscal.Application.Queries;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.UI.Resources;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;

namespace Fiscal.UI.ViewModels;

/// <summary>A product's code as the Codes box edits it.</summary>
public sealed class ProductCodeEditRow(FiscalProductCodeRow row) : ViewModelBase
{
    private FiscalItemCodeType _type = row.Type ?? FiscalItemCodeType.EGS;
    private string _code = row.Code ?? string.Empty;

    public FiscalProductCodeRow Row { get; } = row;
    public string Name => Row.Name;
    public string Sku => Row.Sku;
    public string Barcode => Row.Barcode ?? "–";
    public string UnitName => Row.UnitName;
    public IReadOnlyList<FiscalItemCodeType> Types { get; } = [FiscalItemCodeType.GS1, FiscalItemCodeType.EGS];

    public FiscalItemCodeType Type { get => _type; set { if (Set(ref _type, value)) Raise(nameof(IsChanged)); } }

    public string Code { get => _code; set { if (Set(ref _code, value)) { Raise(nameof(IsChanged)); Raise(nameof(IsMissing)); } } }

    public bool IsMissing => string.IsNullOrWhiteSpace(Code);

    /// <summary>Changed since loaded (only those are saved).</summary>
    public bool IsChanged => Code.Trim() != (Row.Code ?? string.Empty) || (!IsMissing && Type != (Row.Type ?? FiscalItemCodeType.EGS));

    /// <summary>"From the barcode" when the GS1 barcode is used without a saved code.</summary>
    public string SourceText => Row.Source == FiscalCodeSource.Barcode && !IsChanged ? FiscalText.CodeFromBarcode : string.Empty;

    public void Accept(FiscalItemCodeType type, string code) { Type = type; Code = code; Raise(nameof(SourceText)); }
}

/// <summary>A unit's code as the Codes box edits it.</summary>
public sealed class UnitCodeEditRow(FiscalUnitCodeRow row) : ViewModelBase
{
    private string _code = row.Code ?? string.Empty;

    public FiscalUnitCodeRow Row { get; } = row;
    public string Name => Row.Name;
    public string Abbreviation => Row.Abbreviation;
    public string Code { get => _code; set { if (Set(ref _code, value)) { Raise(nameof(IsChanged)); Raise(nameof(IsMissing)); } } }
    public bool IsMissing => Row.IsUsed && string.IsNullOrWhiteSpace(Code);
    public bool IsChanged => Code.Trim().ToUpperInvariant() != (Row.Code ?? string.Empty);
}

/// <summary>A tax rate's classification as the Codes box edits it (Egypt: type and subtype; Saudi Arabia: category and reason).</summary>
public sealed class TaxCodeEditRow(FiscalTaxCodeRow row) : ViewModelBase
{
    private string _egyptType = row.EgyptTaxType ?? string.Empty, _egyptSub = row.EgyptSubType ?? string.Empty;
    private string _saudiCategory = row.SaudiCategory ?? string.Empty, _saudiCode = row.SaudiExemptionCode ?? string.Empty, _saudiReason = row.SaudiExemptionReason ?? string.Empty;

    public FiscalTaxCodeRow Row { get; } = row;
    public string Code => Row.Code;
    public string Name => Row.Name;
    public string RateText => "‎" + (Row.Rate * 100m).ToString("0.##", CultureInfo.CurrentCulture) + "%‎";   // kept in order in Arabic

    public string EgyptTaxType { get => _egyptType; set { if (Set(ref _egyptType, value)) Raise(nameof(IsChanged)); } }
    public string EgyptSubType { get => _egyptSub; set { if (Set(ref _egyptSub, value)) Raise(nameof(IsChanged)); } }
    public string SaudiCategory { get => _saudiCategory; set { if (Set(ref _saudiCategory, value)) { Raise(nameof(IsChanged)); Raise(nameof(NeedsReason)); } } }
    public string SaudiExemptionCode { get => _saudiCode; set { if (Set(ref _saudiCode, value)) Raise(nameof(IsChanged)); } }
    public string SaudiExemptionReason { get => _saudiReason; set { if (Set(ref _saudiReason, value)) Raise(nameof(IsChanged)); } }

    /// <summary>Zero rated, exempt and out of scope need the exemption reason.</summary>
    public bool NeedsReason => SaudiCategory.Trim().ToUpperInvariant() is "Z" or "E" or "O";

    public bool IsChanged => EgyptTaxType.Trim().ToUpperInvariant() != (Row.EgyptTaxType ?? string.Empty) || EgyptSubType.Trim().ToUpperInvariant() != (Row.EgyptSubType ?? string.Empty)
        || SaudiCategory.Trim().ToUpperInvariant() != (Row.SaudiCategory ?? string.Empty) || SaudiExemptionCode.Trim().ToUpperInvariant() != (Row.SaudiExemptionCode ?? string.Empty)
        || SaudiExemptionReason.Trim() != (Row.SaudiExemptionReason ?? string.Empty);
}

/// <summary>Which list the Codes box shows.</summary>
public enum CodesSection
{
    Products,
    Units,
    TaxRates,
}

/// <summary>
/// MISS-11b: the Codes box of the Electronic receipts screen (owner's decisions 2026-10-20: all codes here; a valid GS1 barcode is the code;
/// EGS codes suggested from the SKU and exported for the ETA portal). Egypt needs products, units and tax rates; Saudi Arabia only the tax
/// rates' categories.
/// </summary>
public sealed class FiscalCodesPanel : ViewModelBase
{
    private readonly IUiActionRunner _runner;
    private FiscalCodesDto? _codes;
    private CodesSection _section = CodesSection.Products;
    private bool _onlyMissing;
    private string _missingText = string.Empty;

    public FiscalCodesPanel(IUiActionRunner runner)
    {
        _runner = runner;
        SaveCommand = Command(SaveAsync, () => HasChanges);
        SuggestCommand = Command(() => { Suggest(); return Task.CompletedTask; }, () => IsEgypt && TaxNumber is { Length: 9 } && Products.Any(p => p.IsMissing));
    }

    /// <summary>The shop's tax registration number (for suggested EGS codes); set by the screen from the saved settings.</summary>
    public string? TaxNumber { get; set; }

    public ICommand SaveCommand { get; }
    public ICommand SuggestCommand { get; }

    public ObservableCollection<ProductCodeEditRow> Products { get; } = [];
    public ObservableCollection<ProductCodeEditRow> VisibleProducts { get; } = [];
    public ObservableCollection<UnitCodeEditRow> Units { get; } = [];
    public ObservableCollection<TaxCodeEditRow> TaxRates { get; } = [];

    /// <summary>Common unit codes (UN/ECE) offered in the list; any code of the authority's table can be typed.</summary>
    public IReadOnlyList<string> UnitCodeChoices { get; } = ["EA", "C62", "KGM", "GRM", "LTR", "MLT", "MTR", "CMT", "BX", "PK", "PR", "DZN", "SET"];

    /// <summary>Egypt's tax types and VAT subtypes (V009 = general items) *(to confirm against the ETA code tables)*.</summary>
    public IReadOnlyList<string> EgyptTaxTypeChoices { get; } = ["T1", "T2", "T3", "T4"];
    public IReadOnlyList<string> EgyptSubTypeChoices { get; } = ["V009", "V001", "V002", "V003", "V004", "V005", "V006", "V007", "V008", "V010"];
    public IReadOnlyList<string> SaudiCategoryChoices { get; } = ["S", "Z", "E", "O"];
    public IReadOnlyList<string> SaudiReasonChoices { get; } =
        ["VATEX-SA-29", "VATEX-SA-29-7", "VATEX-SA-30", "VATEX-SA-32", "VATEX-SA-33", "VATEX-SA-34-1", "VATEX-SA-34-2", "VATEX-SA-34-3", "VATEX-SA-34-4",
         "VATEX-SA-34-5", "VATEX-SA-35", "VATEX-SA-36", "VATEX-SA-EDU", "VATEX-SA-HEA", "VATEX-SA-MLTRY", "VATEX-SA-OOS"];

    /// <summary>A country is chosen: the box shows.</summary>
    public bool IsShown => _codes is { Country: not FiscalCountry.None };
    public bool IsEgypt => _codes?.Country == FiscalCountry.Egypt;
    public bool IsSaudi => _codes?.Country == FiscalCountry.SaudiArabia;

    public CodesSection Section
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;
            foreach (var name in new[] { nameof(ShowsProducts), nameof(ShowsUnits), nameof(ShowsTaxRates), nameof(ShowsEgyptTaxRates), nameof(ShowProductsSection), nameof(ShowUnitsSection), nameof(ShowTaxRatesSection) })
                Raise(name);
        }
    }

    // two-way for the Shelf | Damaged-style switch (radio buttons)
    public bool ShowProductsSection { get => Section == CodesSection.Products; set { if (value) Section = CodesSection.Products; } }
    public bool ShowUnitsSection { get => Section == CodesSection.Units; set { if (value) Section = CodesSection.Units; } }
    public bool ShowTaxRatesSection { get => Section == CodesSection.TaxRates; set { if (value) Section = CodesSection.TaxRates; } }

    public bool ShowsProducts => IsEgypt && Section == CodesSection.Products;
    public bool ShowsUnits => IsEgypt && Section == CodesSection.Units;
    public bool ShowsTaxRates => IsSaudi || Section == CodesSection.TaxRates;
    public bool ShowsEgyptTaxRates => IsEgypt && Section == CodesSection.TaxRates;

    public bool OnlyMissing { get => _onlyMissing; set { if (Set(ref _onlyMissing, value)) Filter(); } }

    public string ProductsTabText => Tab(FiscalText.CodesProducts, Products.Count(p => p.IsMissing));
    public string UnitsTabText => Tab(FiscalText.CodesUnits, Units.Count(u => u.IsMissing));
    public string TaxRatesTabText => Tab(FiscalText.CodesTaxRates, _codes?.MissingTaxRates ?? 0);

    /// <summary>"3 products, 1 unit and 2 tax rates still need a code." or "Every code the tax authority needs is set."</summary>
    public string MissingText { get => _missingText; private set => Set(ref _missingText, value); }

    public bool HasChanges => Products.Any(p => p.IsChanged) || Units.Any(u => u.IsChanged) || TaxRates.Any(t => t.IsChanged);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var codes = await _runner.RunAsync((scope, ct) => scope.Get<GetFiscalCodesQueryHandler>().HandleAsync(new GetFiscalCodesQuery(), ct), cancellationToken);
        if (!Accept(codes)) return;
        Show(codes.Value);
    }

    private void Show(FiscalCodesDto codes)
    {
        _codes = codes;
        Products.Clear();
        foreach (var p in codes.Products) Products.Add(Watch(new ProductCodeEditRow(p)));
        Units.Clear();
        foreach (var u in codes.Units) Units.Add(Watch(new UnitCodeEditRow(u)));
        TaxRates.Clear();
        foreach (var t in codes.TaxRates) TaxRates.Add(Watch(new TaxCodeEditRow(t)));
        if (codes.Country == FiscalCountry.SaudiArabia) Section = CodesSection.TaxRates;
        Filter();
        foreach (var name in new[] { nameof(IsShown), nameof(IsEgypt), nameof(IsSaudi), nameof(ShowsProducts), nameof(ShowsUnits), nameof(ShowsTaxRates), nameof(ShowsEgyptTaxRates) }) Raise(name);
        Recount();
    }

    private T Watch<T>(T row) where T : ViewModelBase
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "IsChanged" or "IsMissing") { Raise(nameof(HasChanges)); Recount(); }
        };
        return row;
    }

    private void Recount()
    {
        Raise(nameof(ProductsTabText));
        Raise(nameof(UnitsTabText));
        Raise(nameof(TaxRatesTabText));
        MissingText = _codes is null ? string.Empty
            : _codes.MissingTotal == 0 ? FiscalText.CodesAllSet
            : string.Format(CultureInfo.CurrentCulture, FiscalText.CodesMissing, _codes.MissingProducts, _codes.MissingUnits, _codes.MissingTaxRates);
    }

    private void Filter()
    {
        VisibleProducts.Clear();
        foreach (var p in Products.Where(p => !OnlyMissing || p.IsMissing)) VisibleProducts.Add(p);
    }

    /// <summary>EG-{tax number}-{SKU} for every product still without a code (owner's decision); saved only with "Save codes".</summary>
    private void Suggest()
    {
        var suggested = 0;
        foreach (var p in Products.Where(p => p.IsMissing))
        {
            p.Accept(FiscalItemCodeType.EGS, Egs.Suggest(TaxNumber!, p.Sku));
            suggested++;
        }

        StatusMessage = string.Format(CultureInfo.CurrentCulture, FiscalText.CodesSuggested, suggested);
    }

    private async Task SaveAsync()
    {
        var command = new SaveFiscalCodesCommand(
            Products.Where(p => p.IsChanged).Select(p => new FiscalProductCodeChange(p.Row.ProductId, p.Type, p.Code)).ToList(),
            Units.Where(u => u.IsChanged).Select(u => new FiscalUnitCodeChange(u.Row.UnitId, u.Code)).ToList(),
            TaxRates.Where(t => t.IsChanged).Select(t => new FiscalTaxCodeChange(t.Row.TaxRateId, t.EgyptTaxType, t.EgyptSubType, t.SaudiCategory,
                t.SaudiExemptionCode, t.SaudiExemptionReason)).ToList());
        var saved = await _runner.RunAsync((scope, ct) => scope.Get<SaveFiscalCodesCommandHandler>().HandleAsync(command, ct));
        if (!Accept(saved)) return;

        await LoadAsync(CancellationToken.None);
        if (ErrorMessage is null) StatusMessage = FiscalText.CodesSaved;
    }

    /// <summary>Writes the EGS codes for the ETA portal to <paramref name="path"/> (UTF-8 with a byte-order mark, so spreadsheet programs read Arabic).</summary>
    public Task ExportAsync(string path) => BusyAsync(async () =>
    {
        var csv = await _runner.RunAsync((scope, ct) => scope.Get<ExportEgsCodesQueryHandler>().HandleAsync(new ExportEgsCodesQuery(), ct));
        if (!Accept(csv)) return;
        await File.WriteAllTextAsync(path, csv.Value, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        StatusMessage = string.Format(CultureInfo.CurrentCulture, FiscalText.CodesExported, Path.GetFileName(path));
    });

    private static string Tab(string name, int missing)
        => missing > 0 ? string.Format(CultureInfo.CurrentCulture, FiscalText.CodesTabMissing, name, missing) : name;
}
