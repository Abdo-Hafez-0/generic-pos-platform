using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Queries;
using Inventory.Contracts.Models;

namespace Inventory.UI.ViewModels;

/// <summary>
/// ViewModel for displaying a list of warehouses.
///
/// Communicates through the Application layer (GetWarehousesQueryHandler).
/// Does not reference Inventory.Infrastructure or any EF Core type.
/// Architecture: Inventory.UI → Inventory.Application → Inventory.Domain ← Inventory.Infrastructure
/// </summary>
public sealed class WarehouseListViewModel : INotifyPropertyChanged
{
    private readonly GetWarehousesQueryHandler _queryHandler;
    private bool _isLoading;
    private string? _errorMessage;

    public WarehouseListViewModel(GetWarehousesQueryHandler queryHandler)
    {
        _queryHandler = queryHandler;
        Warehouses = [];
    }

    public ObservableCollection<WarehouseDto> Warehouses { get; }

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
            var result = await _queryHandler.HandleAsync(new GetWarehousesQuery(ActiveOnly: false), cancellationToken);
            Warehouses.Clear();
            if (result.IsSuccess)
            {
                foreach (var warehouse in result.Value)
                    Warehouses.Add(warehouse);
            }
            else
            {
                ErrorMessage = result.Error.Description;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load warehouses: {ex.Message}";
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
