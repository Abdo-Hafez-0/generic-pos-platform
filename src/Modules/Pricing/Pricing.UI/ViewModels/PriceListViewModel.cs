using System.Collections.ObjectModel;
using Pricing.Application.Commands;
using Pricing.Application.DTOs;
using Pricing.Application.Queries;

namespace Pricing.UI.ViewModels;

/// <summary>Minimal pricing screen: show a product's prices, add a price, and look up its current price. Application handlers only.</summary>
public sealed class ProductPricesViewModel(
    ListPricesForProductQueryHandler list,
    CreatePriceCommandHandler create,
    GetCurrentPriceQueryHandler current) : ViewModelBase
{
    private string? _error;
    private decimal? _currentPrice;

    public ObservableCollection<PriceDto> Prices { get; } = [];
    public string? ErrorMessage { get => _error; private set => Set(ref _error, value); }
    public decimal? CurrentPrice { get => _currentPrice; private set => Set(ref _currentPrice, value); }

    public async Task LoadAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        Prices.Clear();
        foreach (var p in await list.HandleAsync(new ListPricesForProductQuery(productId), cancellationToken)) Prices.Add(p);
        CurrentPrice = (await current.HandleAsync(new GetCurrentPriceQuery(productId), cancellationToken))?.Amount;
    }

    public async Task AddPriceAsync(string productCode, decimal amount, DateTime effectiveFrom, Guid productId, CancellationToken cancellationToken = default)
    {
        var r = await create.HandleAsync(new CreatePriceCommand(productCode, amount, effectiveFrom), cancellationToken);
        if (r.IsFailure) { ErrorMessage = r.Error.Description; return; }
        await LoadAsync(productId, cancellationToken);
    }
}
