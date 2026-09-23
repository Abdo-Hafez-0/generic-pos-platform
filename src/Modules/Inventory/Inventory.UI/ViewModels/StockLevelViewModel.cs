using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Queries;
using Inventory.Contracts.Models;

namespace Inventory.UI.ViewModels;

/// <summary>
/// ViewModel for displaying current stock levels.
///
/// Communicates through the Application layer (GetAllStockLevelsQueryHandler).
/// Does not reference Inventory.Infrastructure or any EF Core type.
/// </summary>
public sealed class StockLevelViewModel : INotifyPropertyChanged
{
    private readonly GetAllStockLevelsQueryHandler _queryHandler;
    private bool _isLoading;
    private string? _errorMessage;

    public StockLevelViewModel(GetAllStockLevelsQueryHandler queryHandler)
    {
        _queryHandler = queryHandler;
        StockLevels = [];
    }

    public ObservableCollection<StockLevelDto> StockLevels { get; }

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
            var result = await _queryHandler.HandleAsync(new GetAllStockLevelsQuery(), cancellationToken);
            StockLevels.Clear();
            if (result.IsSuccess)
            {
                foreach (var level in result.Value)
                    StockLevels.Add(level);
            }
            else
            {
                ErrorMessage = result.Error.Description;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load stock levels: {ex.Message}";
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
