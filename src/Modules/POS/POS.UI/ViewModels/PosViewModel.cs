using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;

namespace POS.UI.ViewModels;

/// <summary>
/// Minimal cashier screen view model: product/barcode input, current cart, totals and checkout.
///
/// Talks only to POS.Contracts (IPOSService / IPOSReader). No DbContext, repositories, EF Core or
/// module infrastructure is referenced. Fully offline: no network access of any kind.
/// </summary>
public sealed class PosViewModel : INotifyPropertyChanged
{
    private readonly IPOSService _service;
    private readonly IPOSReader _reader;

    private Guid? _sessionId;
    private Guid? _cartId;
    private string _productCode = string.Empty;
    private decimal _quantity = 1m;
    private decimal _subtotal;
    private decimal _total;
    private string? _statusMessage;
    private string? _errorMessage;
    private string? _hardwareMessage;
    private bool _isBusy;

    public PosViewModel(IPOSService service, IPOSReader reader)
    {
        _service = service;
        _reader = reader;
        Items = [];
    }

    public ObservableCollection<POSCartItemResult> Items { get; }

    public string ProductCode
    {
        get => _productCode;
        set { _productCode = value; OnPropertyChanged(); }
    }

    public decimal Quantity
    {
        get => _quantity;
        set { _quantity = value; OnPropertyChanged(); }
    }

    public decimal Subtotal
    {
        get => _subtotal;
        private set { _subtotal = value; OnPropertyChanged(); }
    }

    public decimal Total
    {
        get => _total;
        private set { _total = value; OnPropertyChanged(); }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>A peripheral problem after a completed sale (for example the receipt could not be printed). Not an error: the sale is valid.</summary>
    public string? HardwareMessage
    {
        get => _hardwareMessage;
        private set { _hardwareMessage = value; OnPropertyChanged(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); }
    }

    public bool HasOpenSession => _sessionId.HasValue;
    public bool CanCheckout => _cartId.HasValue && Items.Count > 0 && !IsBusy;

    /// <summary>Opens a cashier session and starts the first cart.</summary>
    public async Task OpenSessionAsync(string cashierReference, Guid warehouseId, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var session = await _service.OpenSessionAsync(cashierReference, warehouseId, cancellationToken);
            if (!session.IsSuccess) { Fail(session.ErrorMessage); return; }

            _sessionId = session.SessionId;
            var cart = await _service.StartCartAsync(session.SessionId, cancellationToken);
            if (!cart.IsSuccess) { Fail(cart.ErrorMessage); return; }

            _cartId = cart.CartId;
            StatusMessage = "Session opened.";
            await RefreshCartAsync(cancellationToken);
        });
        OnPropertyChanged(nameof(HasOpenSession));
    }

    /// <summary>Adds the typed barcode/SKU with the entered quantity, then clears the input.</summary>
    public async Task AddProductAsync(CancellationToken cancellationToken = default)
    {
        if (_cartId is not { } cartId) { Fail("Open a session first."); return; }

        await RunAsync(async () =>
        {
            var result = await _service.AddProductAsync(cartId, ProductCode, Quantity, cancellationToken);
            if (!result.IsSuccess) { Fail(result.ErrorMessage); return; }

            ProductCode = string.Empty;
            Quantity = 1m;
            StatusMessage = "Item added.";
            await RefreshCartAsync(cancellationToken);
        });
    }

    public async Task RemoveProductAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        if (_cartId is not { } cartId) { Fail("Open a session first."); return; }

        await RunAsync(async () =>
        {
            var result = await _service.RemoveProductAsync(cartId, productId, cancellationToken);
            if (!result.IsSuccess) { Fail(result.ErrorMessage); return; }

            StatusMessage = "Item removed.";
            await RefreshCartAsync(cancellationToken);
        });
    }

    public async Task CheckoutAsync(CancellationToken cancellationToken = default)
    {
        if (_cartId is not { } cartId || _sessionId is not { } sessionId) { Fail("Open a session first."); return; }

        await RunAsync(async () =>
        {
            var result = await _service.CheckoutAsync(cartId, cancellationToken: cancellationToken);
            if (!result.IsSuccess) { Fail(result.ErrorMessage); return; }

            StatusMessage = $"Sale completed ({result.SaleId}).";

            // The sale is complete whatever the peripherals did; tell the cashier what must be done by hand.
            HardwareMessage = result.HardwareNotices is { Count: > 0 } notices
                ? string.Join(" ", notices.Select(n => n.Message))
                : null;

            // Ready for the next customer.
            var next = await _service.StartCartAsync(sessionId, cancellationToken);
            _cartId = next.IsSuccess ? next.CartId : null;
            await RefreshCartAsync(cancellationToken);
        });
    }

    private async Task RefreshCartAsync(CancellationToken cancellationToken)
    {
        POSCartResult? cart = _cartId is { } id
            ? await _reader.GetCartAsync(id, cancellationToken)
            : null;

        Items.Clear();
        if (cart is not null)
            foreach (var item in cart.Items) Items.Add(item);

        Subtotal = cart?.Subtotal ?? 0m;
        Total = cart?.Total ?? 0m;
        OnPropertyChanged(nameof(CanCheckout));
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanCheckout));
        }
    }

    private void Fail(string? message)
    {
        ErrorMessage = message ?? "The operation failed.";
        StatusMessage = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
