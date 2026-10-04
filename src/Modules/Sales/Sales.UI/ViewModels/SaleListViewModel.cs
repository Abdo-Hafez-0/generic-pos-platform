using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sales.Application.DTOs;
using Sales.Application.Queries;

namespace Sales.UI.ViewModels;

/// <summary>
/// ViewModel for displaying a list of recent sales.
///
/// Communicates through the Application layer (GetAllSalesQueryHandler).
/// Does not reference Sales.Infrastructure or any EF Core type.
/// Architecture: Sales.UI → Sales.Application → Sales.Domain ← Sales.Infrastructure
/// </summary>
public sealed class SaleListViewModel : INotifyPropertyChanged
{
    private readonly GetAllSalesQueryHandler _queryHandler;
    private bool _isLoading;
    private string? _errorMessage;

    public SaleListViewModel(GetAllSalesQueryHandler queryHandler)
    {
        _queryHandler = queryHandler;
        Sales = [];
    }

    public ObservableCollection<SaleDto> Sales { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set { _isLoading = value; OnPropertyChanged(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var results = await _queryHandler.HandleAsync(new GetAllSalesQuery(), cancellationToken);
            Sales.Clear();
            foreach (var sale in results)
                Sales.Add(sale);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load sales: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
