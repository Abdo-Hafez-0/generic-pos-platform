using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Catalog.Application.DTOs;
using Catalog.Application.Queries;

namespace Catalog.UI.ViewModels;

/// <summary>
/// ViewModel for the product list view.
/// Communicates through Catalog.Application query handlers.
/// Never accesses CatalogDbContext or Infrastructure directly.
/// </summary>
public sealed class ProductListViewModel : INotifyPropertyChanged
{
    private readonly GetProductByIdQueryHandler _queryHandler;
    private bool _isLoading;
    private string _statusMessage = string.Empty;

    public ObservableCollection<ProductDto> Products { get; } = [];

    public bool IsLoading
    {
        get => _isLoading;
        private set { _isLoading = value; OnPropertyChanged(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProductListViewModel(GetProductByIdQueryHandler queryHandler)
    {
        _queryHandler = queryHandler;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
