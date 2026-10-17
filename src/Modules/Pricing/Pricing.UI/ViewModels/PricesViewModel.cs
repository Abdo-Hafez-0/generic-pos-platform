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

/// <summary>One price list as the list shows it.</summary>
public sealed record PriceListRow(PriceListDto List, string DefaultText, string StatusText)
{
    public bool IsActive => List.Status == PriceListStatus.Active;
}

/// <summary>One price as the product's list shows it (dates are local days; the end day is included).</summary>
public sealed record PriceRow(PriceDto Price, string ListCode, string FromText, string UntilText, string StatusText)
{
    public bool IsActive => Price.Status == PriceStatus.Active;
}

/// <summary>
/// Prices (FIX-01d): the price lists (add, choose the one the till uses, deactivate) and the dated, quantity-based prices of a product
/// (find by SKU or barcode, add, deactivate), with the price the till would charge right now. Through the runner; the handlers authorize
/// (pricing.pricelist.manage, pricing.price.manage) and refuse overlapping prices in plain words.
/// </summary>
public sealed class PricesViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;

    private PriceListRow? _selectedList;
    private string _newListCode = string.Empty;
    private string _newListName = string.Empty;
    private bool _newListDefault;
    private bool _isAddingList;
    private bool _isAddingPrice;

    private string _productCode = string.Empty;
    private PricingProductDto? _product;
    private string? _currentPriceText;
    private PriceRow? _selectedPrice;
    private PriceListDto? _newPriceList;
    private string _newAmount = string.Empty;
    private string _newMinimumQuantity = "1";
    private DateTime _newFrom = DateTime.Today;
    private DateTime? _newUntil;

    public PricesViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        AddListCommand = Command(AddListAsync, () => IsAddingList && !string.IsNullOrWhiteSpace(NewListCode) && !string.IsNullOrWhiteSpace(NewListName));
        FindCommand = Command(FindAsync, () => !string.IsNullOrWhiteSpace(ProductCode));
        AddPriceCommand = Command(AddPriceAsync, () => IsAddingPrice && Product is not null && !string.IsNullOrWhiteSpace(NewAmount));
        // MISS-08c (owner's choices): "New price list" by the title and "New price" open the panel beside the lists; each row's "..." holds
        // what can be done to that row
        NewListCommand = Command(() => { CloseEditor(); IsAddingList = true; return Task.CompletedTask; }, () => !IsEditorOpen);
        NewPriceCommand = Command(() => { CloseEditor(); IsAddingPrice = true; return Task.CompletedTask; }, () => !IsEditorOpen && Product is not null);
        CancelCommand = Command(() => { CloseEditor(); return Task.CompletedTask; }, () => IsEditorOpen);
        SetDefaultRowCommand = Command<PriceListRow>(row => { SelectedList = row; return SetDefaultAsync(); }, row => !IsEditorOpen && row is { IsActive: true, List.IsDefault: false });
        DeactivateListRowCommand = Command<PriceListRow>(row => { SelectedList = row; return DeactivateListAsync(); }, row => !IsEditorOpen && row is { IsActive: true });
        DeactivatePriceRowCommand = Command<PriceRow>(row => { SelectedPrice = row; return DeactivatePriceAsync(); }, row => !IsEditorOpen && row is { IsActive: true });
    }

    public ObservableCollection<PriceListRow> PriceLists { get; } = [];
    public ObservableCollection<PriceListDto> ActiveLists { get; } = [];
    public ObservableCollection<PriceRow> Prices { get; } = [];

    public ICommand AddListCommand { get; }
    public ICommand FindCommand { get; }
    public ICommand AddPriceCommand { get; }
    public ICommand NewListCommand { get; }
    public ICommand NewPriceCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SetDefaultRowCommand { get; }
    public ICommand DeactivateListRowCommand { get; }
    public ICommand DeactivatePriceRowCommand { get; }

    /// <summary>The panel shows a new price list.</summary>
    public bool IsAddingList { get => _isAddingList; private set { if (Set(ref _isAddingList, value)) Raise(nameof(IsEditorOpen)); } }

    /// <summary>The panel shows a new price for the product found.</summary>
    public bool IsAddingPrice { get => _isAddingPrice; private set { if (Set(ref _isAddingPrice, value)) Raise(nameof(IsEditorOpen)); } }

    /// <summary>The panel beside the lists is open.</summary>
    public bool IsEditorOpen => IsAddingList || IsAddingPrice;

    public PriceListRow? SelectedList { get => _selectedList; set => Set(ref _selectedList, value); }
    public string NewListCode { get => _newListCode; set => Set(ref _newListCode, value); }
    public string NewListName { get => _newListName; set => Set(ref _newListName, value); }
    public bool NewListDefault { get => _newListDefault; set => Set(ref _newListDefault, value); }

    public string ProductCode { get => _productCode; set => Set(ref _productCode, value); }

    public PricingProductDto? Product
    {
        get => _product;
        private set { if (Set(ref _product, value)) { Raise(nameof(HasProduct)); Raise(nameof(ProductText)); } }
    }

    public bool HasProduct => Product is not null;

    public string ProductText => Product is null ? string.Empty
        : string.Format(CultureInfo.CurrentCulture, PricingText.CatalogPrice, Product.Name, Product.Sku, Product.CatalogSalePrice);

    public string? CurrentPriceText { get => _currentPriceText; private set => Set(ref _currentPriceText, value); }
    public PriceRow? SelectedPrice { get => _selectedPrice; set => Set(ref _selectedPrice, value); }
    public PriceListDto? NewPriceList { get => _newPriceList; set => Set(ref _newPriceList, value); }
    public string NewAmount { get => _newAmount; set => Set(ref _newAmount, value); }
    public string NewMinimumQuantity { get => _newMinimumQuantity; set => Set(ref _newMinimumQuantity, value); }
    public DateTime NewFrom { get => _newFrom; set => Set(ref _newFrom, value.Date); }

    /// <summary>The last day the price applies (included), or null for no end.</summary>
    public DateTime? NewUntil { get => _newUntil; set => Set(ref _newUntil, value?.Date); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(async () =>
    {
        await ReloadListsAsync(cancellationToken);
        if (Product is not null) await ReloadPricesAsync(cancellationToken);
    });

    private void CloseEditor()
    {
        (NewListCode, NewListName, NewListDefault) = (string.Empty, string.Empty, false);
        (NewAmount, NewMinimumQuantity, NewUntil) = (string.Empty, "1", null);
        IsAddingList = IsAddingPrice = false;
    }

    private async Task AddListAsync()
    {
        var (code, name, makeDefault) = (NewListCode.Trim(), NewListName.Trim(), NewListDefault);
        var added = await _runner.RunAsync(async (scope, ct) => Drop(await scope.Get<CreatePriceListCommandHandler>().HandleAsync(new CreatePriceListCommand(code, name, makeDefault), ct)));
        if (!Accept(added)) return;

        CloseEditor();
        await ReloadListsAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, PricingText.ListAdded, code.ToUpperInvariant());
    }

    private async Task SetDefaultAsync()
    {
        if (SelectedList is not { } row) return;
        var done = await _runner.RunAsync((scope, ct) => scope.Get<SetDefaultPriceListCommandHandler>().HandleAsync(new SetDefaultPriceListCommand(row.List.PriceListId), ct));
        if (!Accept(done)) return;

        await ReloadListsAsync(CancellationToken.None);
        if (Product is not null) await ReloadPricesAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, PricingText.DefaultSet, row.List.Code);
    }

    private async Task DeactivateListAsync()
    {
        if (SelectedList is not { } row) return;
        var done = await _runner.RunAsync((scope, ct) => scope.Get<DeactivatePriceListCommandHandler>().HandleAsync(new DeactivatePriceListCommand(row.List.PriceListId), ct));
        if (!Accept(done)) return;

        await ReloadListsAsync(CancellationToken.None);
        if (Product is not null) await ReloadPricesAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, PricingText.ListDeactivated, row.List.Code);
    }

    private async Task FindAsync()
    {
        var code = ProductCode;
        var found = await _runner.RunAsync((scope, ct) => scope.Get<FindPricingProductQueryHandler>().HandleAsync(new FindPricingProductQuery(code), ct));
        if (!Accept(found)) { Product = null; Prices.Clear(); CurrentPriceText = null; return; }

        Product = found.Value;
        await ReloadPricesAsync(CancellationToken.None);
    }

    private async Task AddPriceAsync()
    {
        if (Product is not { } product) return;
        if (NewPriceList is not { } list) { Fail(ActiveLists.Count == 0 ? PricingText.NoLists : PricingText.ChooseList); return; }
        if (!decimal.TryParse(NewAmount, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) || amount < 0) { Fail(PricingText.AmountInvalid); return; }
        if (!decimal.TryParse(NewMinimumQuantity, NumberStyles.Number, CultureInfo.CurrentCulture, out var minimum) || minimum <= 0) { Fail(PricingText.QuantityInvalid); return; }
        if (NewUntil is { } until && until < NewFrom) { Fail(PricingText.UntilBeforeFrom); return; }

        // local days: from the start of the first day, to the start of the day after the last one (the stored end is exclusive)
        var fromUtc = ToUtc(NewFrom);
        DateTime? toUtc = NewUntil is { } last ? ToUtc(last.AddDays(1)) : null;
        var command = new CreatePriceCommand(product.ProductId.ToString("D"), amount, fromUtc, toUtc, minimum, list.PriceListId);
        var added = await _runner.RunAsync(async (scope, ct) => Drop(await scope.Get<CreatePriceCommandHandler>().HandleAsync(command, ct)));
        if (!Accept(added)) return;

        CloseEditor();
        await ReloadPricesAsync(CancellationToken.None);
        StatusMessage = PricingText.PriceAdded;
    }

    private async Task DeactivatePriceAsync()
    {
        if (SelectedPrice is not { } row) return;
        var done = await _runner.RunAsync((scope, ct) => scope.Get<DeactivatePriceCommandHandler>().HandleAsync(new DeactivatePriceCommand(row.Price.PriceId), ct));
        if (!Accept(done)) return;

        await ReloadPricesAsync(CancellationToken.None);
        StatusMessage = PricingText.PriceDeactivated;
    }

    private async Task ReloadListsAsync(CancellationToken cancellationToken)
    {
        var lists = await _runner.QueryAsync((scope, ct) => scope.Get<ListPriceListsQueryHandler>().HandleAsync(new ListPriceListsQuery(), ct), cancellationToken);
        if (!Accept(lists)) return;

        var (selectedId, newListId) = (SelectedList?.List.PriceListId, NewPriceList?.PriceListId);
        PriceLists.Clear();
        ActiveLists.Clear();
        foreach (var list in lists.Value.OrderByDescending(l => l.IsDefault).ThenBy(l => l.Code, StringComparer.CurrentCultureIgnoreCase))
        {
            PriceLists.Add(new PriceListRow(list, list.IsDefault ? PricingText.Yes : string.Empty, list.Status == PriceListStatus.Active ? PricingText.Active : PricingText.Inactive));
            if (list.Status == PriceListStatus.Active) ActiveLists.Add(list);
        }

        SelectedList = PriceLists.FirstOrDefault(l => l.List.PriceListId == selectedId);
        NewPriceList = ActiveLists.FirstOrDefault(l => l.PriceListId == newListId) ?? ActiveLists.FirstOrDefault(l => l.IsDefault) ?? ActiveLists.FirstOrDefault();
    }

    private async Task ReloadPricesAsync(CancellationToken cancellationToken)
    {
        if (Product is not { } product) return;
        var loaded = await _runner.QueryAsync(async (scope, ct) => (
            Prices: await scope.Get<ListPricesForProductQueryHandler>().HandleAsync(new ListPricesForProductQuery(product.ProductId), ct),
            Now: await scope.Get<GetCurrentPriceQueryHandler>().HandleAsync(new GetCurrentPriceQuery(product.ProductId), ct)), cancellationToken);
        if (!Accept(loaded)) return;

        var codes = PriceLists.ToDictionary(l => l.List.PriceListId, l => l.List.Code);
        Prices.Clear();
        foreach (var price in loaded.Value.Prices)
            Prices.Add(new PriceRow(price, codes.GetValueOrDefault(price.PriceListId, string.Empty), LocalDay(price.EffectiveFrom),
                price.EffectiveTo is { } to ? LocalDay(to.AddTicks(-1)) : string.Empty, price.Status == PriceStatus.Active ? PricingText.Active : PricingText.Inactive));
        SelectedPrice = null;

        CurrentPriceText = loaded.Value.Now is { } now
            ? string.Format(CultureInfo.CurrentCulture, PricingText.PriceNow, now.Amount, now.PriceListCode)
            : string.Format(CultureInfo.CurrentCulture, PricingText.CatalogApplies, product.CatalogSalePrice);
    }

    private static DateTime ToUtc(DateTime localDay) => DateTime.SpecifyKind(localDay, DateTimeKind.Local).ToUniversalTime();

    /// <summary>The local day between left-to-right marks: in Arabic the date keeps its order (MISS-08c).</summary>
    private static string LocalDay(DateTime utc) => "‎" + DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("d", CultureInfo.CurrentCulture) + "‎";

    private static Result Drop<T>(Result<T> result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error);

    private void Fail(string message)
    {
        ErrorMessage = message;
        StatusMessage = null;
    }
}
