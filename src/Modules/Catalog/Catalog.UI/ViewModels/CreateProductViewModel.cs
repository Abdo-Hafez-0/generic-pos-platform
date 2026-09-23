using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Catalog.Application.Commands;
using Catalog.Application.DTOs;
using Catalog.Application.Queries;
using Catalog.Domain.ValueObjects;
using Platform.Core.Results;

namespace Catalog.UI.ViewModels;

/// <summary>
/// ViewModel for the product creation form.
/// Uses Catalog.Application command and query handlers.
/// Never accesses CatalogDbContext or any Infrastructure type.
/// </summary>
public sealed class CreateProductViewModel : INotifyPropertyChanged
{
    private readonly CreateProductCommandHandler _createHandler;
    private readonly GetAllCategoriesQueryHandler _categoriesHandler;
    private readonly GetAllUnitsQueryHandler _unitsHandler;

    private string _sku = string.Empty;
    private string _name = string.Empty;
    private string _description = string.Empty;
    private decimal _salePrice;
    private decimal? _costPrice;
    private Guid _selectedCategoryId;
    private Guid _selectedUnitId;
    private bool _isBusy;
    private string _errorMessage = string.Empty;
    private string _successMessage = string.Empty;

    public string Sku
    {
        get => _sku;
        set { _sku = value; OnPropertyChanged(); }
    }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public decimal SalePrice
    {
        get => _salePrice;
        set { _salePrice = value; OnPropertyChanged(); }
    }

    public decimal? CostPrice
    {
        get => _costPrice;
        set { _costPrice = value; OnPropertyChanged(); }
    }

    public Guid SelectedCategoryId
    {
        get => _selectedCategoryId;
        set { _selectedCategoryId = value; OnPropertyChanged(); }
    }

    public Guid SelectedUnitId
    {
        get => _selectedUnitId;
        set { _selectedUnitId = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    public string SuccessMessage
    {
        get => _successMessage;
        private set { _successMessage = value; OnPropertyChanged(); }
    }

    public List<CategoryDto> Categories { get; private set; } = [];
    public List<UnitDto> Units { get; private set; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? ProductCreated;

    public CreateProductViewModel(
        CreateProductCommandHandler createHandler,
        GetAllCategoriesQueryHandler categoriesHandler,
        GetAllUnitsQueryHandler unitsHandler)
    {
        _createHandler = createHandler;
        _categoriesHandler = categoriesHandler;
        _unitsHandler = unitsHandler;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var categoriesResult = await _categoriesHandler.HandleAsync(new GetAllCategoriesQuery(), cancellationToken);
        var unitsResult = await _unitsHandler.HandleAsync(new GetAllUnitsQuery(), cancellationToken);

        if (categoriesResult.IsSuccess) Categories = [..categoriesResult.Value];
        if (unitsResult.IsSuccess) Units = [..unitsResult.Value];

        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(Units));
    }

    public async Task<bool> CreateProductAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = string.Empty;
        SuccessMessage = string.Empty;
        IsBusy = true;

        try
        {
            var command = new CreateProductCommand(
                Sku,
                Name,
                SelectedCategoryId,
                SelectedUnitId,
                SalePrice,
                CostPrice,
                string.IsNullOrWhiteSpace(Description) ? null : Description);

            var result = await _createHandler.HandleAsync(command, cancellationToken);

            if (result.IsFailure)
            {
                ErrorMessage = result.Error.Description;
                return false;
            }

            SuccessMessage = $"Product '{Name}' created successfully.";
            ProductCreated?.Invoke();
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
