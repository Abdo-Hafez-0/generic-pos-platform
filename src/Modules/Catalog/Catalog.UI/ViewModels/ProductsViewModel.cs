using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Catalog.Application.Commands;
using Catalog.Application.DTOs;
using Catalog.Application.Queries;
using Catalog.Application.Security;
using Catalog.Domain.Enums;
using Catalog.UI.Resources;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Catalog.UI.ViewModels;

/// <summary>One product as the list shows it.</summary>
public sealed record ProductRow(ProductDto Product, string StatusText, string BarcodesText)
{
    public bool IsActive => Product.Status == ProductStatus.Active;
}

/// <summary>
/// The products screen (FIX-01c): search by name, SKU or barcode; create and edit products (with an added barcode); deactivate.
/// Every action runs through <see cref="IUiActionRunner"/> (one scope per click) and the Catalog handlers, which authorize. Cost prices
/// follow catalog.cost.view: without it the field is not shown, and saving keeps the stored cost (the handler enforces that too).
/// </summary>
public sealed class ProductsViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;

    private string _searchText = string.Empty;
    private bool _includeInactive;
    private ProductRow? _selected;
    private string? _resultInfo;
    private bool _canSeeCost;

    private bool _isEditorOpen;
    private Guid? _editingId;
    private string _editSku = string.Empty;
    private string _editName = string.Empty;
    private CategoryDto? _editCategory;
    private UnitDto? _editUnit;
    private string _editSalePrice = string.Empty;
    private string _editCostPrice = string.Empty;
    private string _editDescription = string.Empty;
    private string _editBarcode = string.Empty;

    public ProductsViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        SearchCommand = Command(SearchAsync);
        NewCommand = Command(() => { OpenEditor(null); return Task.CompletedTask; }, () => !IsEditorOpen);
        EditCommand = Command(() => { OpenEditor(Selected); return Task.CompletedTask; }, () => !IsEditorOpen && Selected is not null);
        SaveCommand = Command(SaveAsync, () => IsEditorOpen);
        CancelCommand = Command(() => { IsEditorOpen = false; return Task.CompletedTask; }, () => IsEditorOpen);
        DeactivateCommand = Command(DeactivateAsync, () => !IsEditorOpen && Selected is { IsActive: true });
    }

    public ObservableCollection<ProductRow> Products { get; } = [];
    public ObservableCollection<CategoryDto> Categories { get; } = [];
    public ObservableCollection<UnitDto> Units { get; } = [];

    public ICommand SearchCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeactivateCommand { get; }

    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public bool IncludeInactive { get => _includeInactive; set => Set(ref _includeInactive, value); }
    public ProductRow? Selected { get => _selected; set => Set(ref _selected, value); }

    /// <summary>"No product matches" or "only the first N are shown", or null.</summary>
    public string? ResultInfo { get => _resultInfo; private set => Set(ref _resultInfo, value); }

    /// <summary>True when the signed-in user holds catalog.cost.view (the cost field and column are shown).</summary>
    public bool CanSeeCost { get => _canSeeCost; private set { if (Set(ref _canSeeCost, value)) Raise(nameof(CostHidden)); } }

    public bool CostHidden => !CanSeeCost;

    /// <summary>True when there is no category or no unit yet (a product cannot be created; the screen says where to add them).</summary>
    public bool NeedsCategoriesAndUnits => Categories.Count == 0 || Units.Count == 0;

    public bool IsEditorOpen { get => _isEditorOpen; private set { if (Set(ref _isEditorOpen, value)) Raise(nameof(IsListMode)); } }
    public bool IsListMode => !IsEditorOpen;
    public bool IsNewProduct => _editingId is null;

    public string EditSku { get => _editSku; set => Set(ref _editSku, value); }
    public string EditName { get => _editName; set => Set(ref _editName, value); }
    public CategoryDto? EditCategory { get => _editCategory; set => Set(ref _editCategory, value); }
    public UnitDto? EditUnit { get => _editUnit; set => Set(ref _editUnit, value); }
    public string EditSalePrice { get => _editSalePrice; set => Set(ref _editSalePrice, value); }
    public string EditCostPrice { get => _editCostPrice; set => Set(ref _editCostPrice, value); }
    public string EditDescription { get => _editDescription; set => Set(ref _editDescription, value); }

    /// <summary>An optional barcode to add when saving (existing barcodes stay).</summary>
    public string EditBarcode { get => _editBarcode; set => Set(ref _editBarcode, value); }

    public string ExistingBarcodes => _editingId is { } id ? Products.FirstOrDefault(p => p.Product.Id == id)?.BarcodesText ?? string.Empty : string.Empty;

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(async () =>
    {
        var loaded = await _runner.QueryAsync(async (scope, ct) =>
        {
            var categories = await scope.Get<GetAllCategoriesQueryHandler>().HandleAsync(new GetAllCategoriesQuery(), ct);
            var units = await scope.Get<GetAllUnitsQueryHandler>().HandleAsync(new GetAllUnitsQuery(), ct);
            var canSeeCost = await scope.Get<IAuthorizationService>().IsAllowedAsync(CatalogCapabilities.ViewCost, ct);
            var products = await scope.Get<SearchProductsQueryHandler>().HandleAsync(new SearchProductsQuery(SearchText, IncludeInactive), ct);
            return (Categories: categories, Units: units, CanSeeCost: canSeeCost, Products: products);
        }, cancellationToken);
        if (!Accept(loaded)) return;

        var (categories, units, canSeeCost, products) = loaded.Value;
        Replace(Categories, categories.IsSuccess ? categories.Value.Where(c => c.IsActive) : []);
        Replace(Units, units.IsSuccess ? units.Value.Where(u => u.IsActive) : []);
        Raise(nameof(NeedsCategoriesAndUnits));
        CanSeeCost = canSeeCost;
        if (Accept(products)) ShowProducts(products.Value);
    });

    private async Task SearchAsync()
    {
        var text = SearchText;
        var includeInactive = IncludeInactive;
        var found = await _runner.RunAsync((scope, ct) => scope.Get<SearchProductsQueryHandler>().HandleAsync(new SearchProductsQuery(text, includeInactive), ct));
        if (Accept(found)) ShowProducts(found.Value);
    }

    private void OpenEditor(ProductRow? row)
    {
        var product = row?.Product;
        _editingId = product?.Id;
        EditSku = product?.Sku ?? string.Empty;
        EditName = product?.Name ?? string.Empty;
        EditCategory = Categories.FirstOrDefault(c => c.Id == product?.CategoryId) ?? (Categories.Count == 1 ? Categories[0] : null);
        EditUnit = Units.FirstOrDefault(u => u.Id == product?.UnitId) ?? (Units.Count == 1 ? Units[0] : null);
        EditSalePrice = product?.SalePrice.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
        EditCostPrice = product?.CostPrice?.ToString("0.##", CultureInfo.CurrentCulture) ?? string.Empty;
        EditDescription = product?.Description ?? string.Empty;
        EditBarcode = string.Empty;
        ErrorMessage = null;
        StatusMessage = null;
        Raise(nameof(IsNewProduct));
        Raise(nameof(ExistingBarcodes));
        IsEditorOpen = true;
    }

    private async Task SaveAsync()
    {
        if (EditCategory is not { } category || EditUnit is not { } unit) { Fail(CatalogText.ChooseCategoryUnit); return; }
        if (!decimal.TryParse(EditSalePrice, NumberStyles.Number, CultureInfo.CurrentCulture, out var salePrice) || salePrice < 0) { Fail(CatalogText.PriceInvalid); return; }

        decimal? costPrice = null;
        if (CanSeeCost && !string.IsNullOrWhiteSpace(EditCostPrice))
        {
            if (!decimal.TryParse(EditCostPrice, NumberStyles.Number, CultureInfo.CurrentCulture, out var cost) || cost < 0) { Fail(CatalogText.CostInvalid); return; }
            costPrice = cost;
        }

        var editingId = _editingId;
        var (sku, name, description, barcode) = (EditSku, EditName, Blank(EditDescription), EditBarcode.Trim());
        var search = (SearchText, IncludeInactive);

        var saved = await _runner.RunAsync<ProductSearchResult>(async (scope, ct) =>
        {
            Guid productId;
            if (editingId is { } id)
            {
                var updated = await scope.Get<UpdateProductCommandHandler>().HandleAsync(new UpdateProductCommand(id, name, category.Id, unit.Id, salePrice, costPrice, description), ct);
                if (updated.IsFailure) return Result.Failure<ProductSearchResult>(updated.Error);
                productId = id;
            }
            else
            {
                var created = await scope.Get<CreateProductCommandHandler>().HandleAsync(new CreateProductCommand(sku, name, category.Id, unit.Id, salePrice, costPrice, description), ct);
                if (created.IsFailure) return Result.Failure<ProductSearchResult>(created.Error);
                productId = created.Value.Value;
            }

            if (barcode.Length > 0)
            {
                var assigned = await scope.Get<AssignBarcodeCommandHandler>().HandleAsync(new AssignBarcodeCommand(productId, barcode, GuessFormat(barcode)), ct);
                if (assigned.IsFailure) return Result.Failure<ProductSearchResult>(assigned.Error);
            }

            return await scope.Get<SearchProductsQueryHandler>().HandleAsync(new SearchProductsQuery(search.SearchText, search.IncludeInactive), ct);
        });

        if (!Accept(saved))
        {
            // a new product that was created before its barcode failed now exists: edit it from here on instead of creating it twice
            if (editingId is null) await RefreshEditingAfterPartialCreateAsync(sku);
            return;
        }

        IsEditorOpen = false;
        ShowProducts(saved.Value);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, editingId is null ? CatalogText.Created : CatalogText.Saved, name.Trim());
    }

    private async Task RefreshEditingAfterPartialCreateAsync(string sku)
    {
        var existing = await _runner.RunAsync((scope, ct) => scope.Get<GetProductBySkuQueryHandler>().HandleAsync(new GetProductBySkuQuery(sku), ct));
        if (existing.IsSuccess)
        {
            _editingId = existing.Value.Id;
            Raise(nameof(IsNewProduct));
        }
    }

    private async Task DeactivateAsync()
    {
        if (Selected is not { } row) return;

        var search = (SearchText, IncludeInactive);
        var done = await _runner.RunAsync<ProductSearchResult>(async (scope, ct) =>
        {
            var deactivated = await scope.Get<DeactivateProductCommandHandler>().HandleAsync(new DeactivateProductCommand(row.Product.Id), ct);
            if (deactivated.IsFailure) return Result.Failure<ProductSearchResult>(deactivated.Error);
            return await scope.Get<SearchProductsQueryHandler>().HandleAsync(new SearchProductsQuery(search.SearchText, search.IncludeInactive), ct);
        });

        if (!Accept(done)) return;
        ShowProducts(done.Value);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, CatalogText.Deactivated, row.Product.Name);
    }

    private void ShowProducts(ProductSearchResult result)
    {
        var selectedId = Selected?.Product.Id;
        Replace(Products, result.Items.Select(p => new ProductRow(p, Describe(p.Status), string.Join(", ", p.Barcodes.Select(b => b.Value)))));
        Selected = Products.FirstOrDefault(p => p.Product.Id == selectedId);
        ResultInfo = result.IsTruncated
            ? string.Format(CultureInfo.CurrentCulture, CatalogText.Truncated, result.Items.Count)
            : result.Items.Count == 0 ? CatalogText.NoProducts : null;
    }

    /// <summary>The barcode format from its shape (the format is descriptive; the value is what scanners match).</summary>
    public static BarcodeFormat GuessFormat(string value)
        => value.All(char.IsAsciiDigit) ? value.Length switch { 13 => BarcodeFormat.EAN13, 8 => BarcodeFormat.EAN8, 12 => BarcodeFormat.UPC, _ => BarcodeFormat.Code128 }
            : BarcodeFormat.Code128;

    private static string Describe(ProductStatus status) => status switch
    {
        ProductStatus.Active => CatalogText.StatusActive,
        ProductStatus.Inactive => CatalogText.StatusInactive,
        _ => CatalogText.StatusDiscontinued,
    };

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        StatusMessage = null;
    }
}
