using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using Pricing.Application.Commands;
using Pricing.Application.DTOs;
using Pricing.Application.Queries;
using Pricing.Domain.Enums;
using Pricing.UI.Resources;

namespace Pricing.UI.ViewModels;

/// <summary>One tax rate as the list shows it.</summary>
public sealed record TaxRateRow(TaxRateDto Rate, string PercentText, string DefaultText, string StatusText)
{
    public bool IsActive => Rate.Status == TaxRateStatus.Active;
}

/// <summary>A choice of tax rate for a product: a rate, or null = "use the default rate".</summary>
public sealed record TaxChoice(TaxRateDto? Rate, string Text);

/// <summary>
/// Tax rates (FIX-08a): the named rates (add, change the percentage, choose the default, deactivate) and the rate of each product (find by
/// SKU or barcode, choose a rate or the default). Prices INCLUDE tax; the till snapshots the rate of every line, so a change here never
/// rewrites a past sale. Through the runner; the handlers authorize (pricing.tax.manage) and refuse bad input in plain words.
/// </summary>
public sealed class TaxRatesViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;

    private TaxRateRow? _selectedRate;
    private string _newCode = string.Empty;
    private string _newName = string.Empty;
    private string _newPercent = string.Empty;
    private bool _newDefault;
    private string _editName = string.Empty;
    private string _editPercent = string.Empty;

    private string _productCode = string.Empty;
    private PricingProductDto? _product;
    private string? _productTaxText;
    private TaxChoice? _productChoice;

    public TaxRatesViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        AddCommand = Command(AddAsync, () => !string.IsNullOrWhiteSpace(NewCode) && !string.IsNullOrWhiteSpace(NewName) && !string.IsNullOrWhiteSpace(NewPercent));
        UpdateCommand = Command(UpdateAsync, () => SelectedRate is { IsActive: true } && !string.IsNullOrWhiteSpace(EditName) && !string.IsNullOrWhiteSpace(EditPercent));
        SetDefaultCommand = Command(SetDefaultAsync, () => SelectedRate is { IsActive: true, Rate.IsDefault: false });
        DeactivateCommand = Command(DeactivateAsync, () => SelectedRate is { IsActive: true, Rate.IsDefault: false });
        FindCommand = Command(FindAsync, () => !string.IsNullOrWhiteSpace(ProductCode));
        SaveProductCommand = Command(SaveProductAsync, () => Product is not null && ProductChoice is not null);
    }

    public ObservableCollection<TaxRateRow> Rates { get; } = [];

    /// <summary>The choices for a product: "use the default" first, then every active rate.</summary>
    public ObservableCollection<TaxChoice> Choices { get; } = [];

    public ICommand AddCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand SetDefaultCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand FindCommand { get; }
    public ICommand SaveProductCommand { get; }

    public TaxRateRow? SelectedRate
    {
        get => _selectedRate;
        set
        {
            if (!Set(ref _selectedRate, value)) return;
            EditName = value?.Rate.Name ?? string.Empty;
            EditPercent = value is null ? string.Empty : Percent(value.Rate.Rate);
        }
    }

    public string NewCode { get => _newCode; set => Set(ref _newCode, value); }
    public string NewName { get => _newName; set => Set(ref _newName, value); }

    /// <summary>The percentage as typed, e.g. "14" or "14.25".</summary>
    public string NewPercent { get => _newPercent; set => Set(ref _newPercent, value); }

    public bool NewDefault { get => _newDefault; set => Set(ref _newDefault, value); }
    public string EditName { get => _editName; set => Set(ref _editName, value); }
    public string EditPercent { get => _editPercent; set => Set(ref _editPercent, value); }

    public string ProductCode { get => _productCode; set => Set(ref _productCode, value); }

    public PricingProductDto? Product
    {
        get => _product;
        private set { if (Set(ref _product, value)) Raise(nameof(HasProduct)); }
    }

    public bool HasProduct => Product is not null;

    /// <summary>"Cola (COLA-1): 14% (STD, the default)" - the rate the till applies to the product now.</summary>
    public string? ProductTaxText { get => _productTaxText; private set => Set(ref _productTaxText, value); }

    public TaxChoice? ProductChoice { get => _productChoice; set => Set(ref _productChoice, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(async () =>
    {
        await ReloadRatesAsync(cancellationToken);
        if (Product is not null) await ReloadProductAsync(cancellationToken);
    });

    private async Task AddAsync()
    {
        if (!TryRate(NewPercent, out var rate)) { Fail(PricingText.TaxPercentInvalid); return; }

        var (code, name, makeDefault) = (NewCode.Trim(), NewName.Trim(), NewDefault);
        var added = await _runner.RunAsync(async (scope, ct) => Drop(await scope.Get<CreateTaxRateCommandHandler>().HandleAsync(new CreateTaxRateCommand(code, name, rate, makeDefault), ct)));
        if (!Accept(added)) return;

        (NewCode, NewName, NewPercent, NewDefault) = (string.Empty, string.Empty, string.Empty, false);
        await ReloadRatesAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, PricingText.TaxRateAdded, code.ToUpperInvariant());
    }

    private async Task UpdateAsync()
    {
        if (SelectedRate is not { } row) return;
        if (!TryRate(EditPercent, out var rate)) { Fail(PricingText.TaxPercentInvalid); return; }

        var name = EditName.Trim();
        var done = await _runner.RunAsync((scope, ct) => scope.Get<UpdateTaxRateCommandHandler>().HandleAsync(new UpdateTaxRateCommand(row.Rate.TaxRateId, name, rate), ct));
        if (!Accept(done)) return;

        await ReloadRatesAsync(CancellationToken.None);
        if (Product is not null) await ReloadProductAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, PricingText.TaxRateChanged, row.Rate.Code);
    }

    private async Task SetDefaultAsync()
    {
        if (SelectedRate is not { } row) return;
        var done = await _runner.RunAsync((scope, ct) => scope.Get<SetDefaultTaxRateCommandHandler>().HandleAsync(new SetDefaultTaxRateCommand(row.Rate.TaxRateId), ct));
        if (!Accept(done)) return;

        await ReloadRatesAsync(CancellationToken.None);
        if (Product is not null) await ReloadProductAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, PricingText.TaxDefaultSet, row.Rate.Code);
    }

    private async Task DeactivateAsync()
    {
        if (SelectedRate is not { } row) return;
        var done = await _runner.RunAsync((scope, ct) => scope.Get<DeactivateTaxRateCommandHandler>().HandleAsync(new DeactivateTaxRateCommand(row.Rate.TaxRateId), ct));
        if (!Accept(done)) return;

        await ReloadRatesAsync(CancellationToken.None);
        if (Product is not null) await ReloadProductAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, PricingText.TaxRateDeactivated, row.Rate.Code);
    }

    private async Task FindAsync()
    {
        var code = ProductCode;
        var found = await _runner.RunAsync((scope, ct) => scope.Get<FindPricingProductQueryHandler>().HandleAsync(new FindPricingProductQuery(code), ct));
        if (!Accept(found)) { Product = null; ProductTaxText = null; ProductChoice = null; return; }

        Product = found.Value;
        await ReloadProductAsync(CancellationToken.None);
    }

    private async Task SaveProductAsync()
    {
        if (Product is not { } product || ProductChoice is not { } choice) return;
        var done = await _runner.RunAsync((scope, ct) => scope.Get<SetProductTaxRateCommandHandler>().HandleAsync(
            new SetProductTaxRateCommand(product.ProductId.ToString("D"), choice.Rate?.TaxRateId), ct));
        if (!Accept(done)) return;

        await ReloadProductAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, PricingText.ProductTaxSaved, product.Sku);
    }

    private async Task ReloadRatesAsync(CancellationToken cancellationToken)
    {
        var rates = await _runner.QueryAsync((scope, ct) => scope.Get<ListTaxRatesQueryHandler>().HandleAsync(new ListTaxRatesQuery(), ct), cancellationToken);
        if (!Accept(rates)) return;

        var selectedId = SelectedRate?.Rate.TaxRateId;
        Rates.Clear();
        foreach (var rate in rates.Value)
            Rates.Add(new TaxRateRow(rate, Percent(rate.Rate) + " %", rate.IsDefault ? PricingText.Yes : string.Empty,
                rate.Status == TaxRateStatus.Active ? PricingText.Active : PricingText.Inactive));
        SelectedRate = Rates.FirstOrDefault(r => r.Rate.TaxRateId == selectedId);

        var chosenId = ProductChoice?.Rate?.TaxRateId;
        Choices.Clear();
        Choices.Add(new TaxChoice(null, PricingText.UseDefaultTax));
        foreach (var rate in rates.Value.Where(r => r.Status == TaxRateStatus.Active))
            Choices.Add(new TaxChoice(rate, $"{rate.Code} - {rate.Name} ({Percent(rate.Rate)} %)"));
        ProductChoice = Choices.FirstOrDefault(c => c.Rate?.TaxRateId == chosenId) ?? (ProductChoice is null ? null : Choices[0]);
    }

    private async Task ReloadProductAsync(CancellationToken cancellationToken)
    {
        if (Product is not { } product) return;
        var tax = await _runner.QueryAsync((scope, ct) => scope.Get<GetProductTaxQueryHandler>().HandleAsync(new GetProductTaxQuery(product.ProductId), ct), cancellationToken);
        if (!Accept(tax)) return;

        ProductChoice = Choices.FirstOrDefault(c => c.Rate?.TaxRateId == tax.Value.Assigned?.TaxRateId && tax.Value.Assigned is { Status: TaxRateStatus.Active })
            ?? Choices.FirstOrDefault();
        ProductTaxText = tax.Value.Effective is { } effective
            ? string.Format(CultureInfo.CurrentCulture, tax.Value.Assigned is { Status: TaxRateStatus.Active } ? PricingText.ProductTaxOwn : PricingText.ProductTaxDefault,
                product.Name, product.Sku, Percent(effective.Rate), effective.Code)
            : string.Format(CultureInfo.CurrentCulture, PricingText.ProductTaxNone, product.Name, product.Sku);
    }

    /// <summary>"14" / "14.25" (current culture) -> 0.14 / 0.1425. The domain checks the range and precision.</summary>
    private static bool TryRate(string percent, out decimal rate)
    {
        rate = 0m;
        if (!decimal.TryParse(percent.Trim().TrimEnd('%').Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out var value)) return false;
        rate = value / 100m;
        return true;
    }

    private static string Percent(decimal rate) => (rate * 100m).ToString("0.##", CultureInfo.CurrentCulture);

    private static Result Drop<T>(Result<T> result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error);

    private void Fail(string message)
    {
        ErrorMessage = message;
        StatusMessage = null;
    }
}
