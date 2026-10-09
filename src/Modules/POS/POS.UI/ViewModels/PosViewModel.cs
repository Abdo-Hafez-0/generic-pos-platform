using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using POS.UI.Resources;

namespace POS.UI.ViewModels;

/// <summary>A way of giving a discount, as the screen offers it (FIX-08c).</summary>
public sealed record DiscountChoice(POSDiscountKind Kind, string Text);

/// <summary>
/// The cashier screen (FIX-01b): open or resume the signed-in user's till, add products by barcode/SKU, remove lines, check out, close the till.
///
/// Talks only to POS.Contracts (IPOSService / IPOSReader), and only through <see cref="IUiActionRunner"/>: every click runs in its own DI
/// scope, so the screen can stay open all day without holding a database context, and an unexpected failure shows a plain sentence.
/// The cashier is whoever is signed in (the session command attributes the till to that user, whatever the screen passes). Fully offline.
///
/// Barcode scanner (FIX-02): while the screen is shown it listens to <see cref="IPOSBarcodeInput"/>, which adds every scan to the open cart
/// exactly like a typed code; the screen then shows the cart again, or the reason a scan was refused. No scanner: the cashier types codes.
/// </summary>
public sealed class PosViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private readonly ICurrentUser _currentUser;
    private readonly IPOSBarcodeInput? _scanner;
    private SynchronizationContext? _ui;
    private bool _listening;
    private bool _scannerReady;

    private Guid? _sessionId;
    private Guid? _cartId;
    private POSWarehouseResult? _selectedWarehouse;
    private string _productCode = string.Empty;
    private string _quantityText = "1";
    private decimal _subtotal;
    private decimal _total;
    private decimal _taxTotal;
    private string? _hardwareMessage;
    private bool _hasNoWarehouses;
    private decimal _discountTotal;
    private bool _canGiveDiscounts;
    private string _discountText = string.Empty;
    private DiscountChoice _discountKind;
    private POSCartItemResult? _selectedItem;
    private PaymentChoice _paymentMethod;
    private string _paymentAmountText = string.Empty;
    private string _paymentNote = string.Empty;
    private string _customerSearch = string.Empty;
    private POSCustomerResult? _foundCustomer;
    private string? _customerText;

    public PosViewModel(IUiActionRunner runner, ICurrentUser currentUser, IPOSBarcodeInput? scanner = null)
    {
        _runner = runner;
        _currentUser = currentUser;
        _scanner = scanner;

        OpenSessionCommand = Command(OpenSessionAsync, () => !HasOpenSession && SelectedWarehouse is not null);
        AddCommand = Command(AddProductAsync, () => HasOpenSession && !string.IsNullOrWhiteSpace(ProductCode));
        RemoveCommand = Command<POSCartItemResult>(item => item is null ? Task.CompletedTask : RemoveProductAsync(item.ProductId), item => item is not null && HasOpenSession);
        DiscountKinds = [new(POSDiscountKind.Percent, PosText.DiscountPercent), new(POSDiscountKind.Amount, PosText.DiscountAmount)];
        _discountKind = DiscountKinds[0];
        LineDiscountCommand = Command(GiveLineDiscountAsync, () => CanGiveDiscounts && HasOpenSession && SelectedItem is not null && !string.IsNullOrWhiteSpace(DiscountText));
        CartDiscountCommand = Command(GiveCartDiscountAsync, () => CanGiveDiscounts && HasOpenSession && Items.Count > 0 && !string.IsNullOrWhiteSpace(DiscountText));
        PaymentMethods = [new(POSPaymentMethod.Cash, PosText.PayCash), new(POSPaymentMethod.Card, PosText.PayCard), new(POSPaymentMethod.Other, PosText.PayOther)];
        _paymentMethod = PaymentMethods[0];
        AddPaymentCommand = Command(AddPaymentAsync, () => HasOpenSession && Items.Count > 0 && AmountDue > 0m && !string.IsNullOrWhiteSpace(PaymentAmountText));
        RemovePaymentCommand = Command<PaymentPart>(part => { if (part is not null) RemovePayment(part); return Task.CompletedTask; }, part => part is not null);
        FindCustomerCommand = Command(FindCustomerAsync, () => HasOpenSession && _cartId.HasValue && !string.IsNullOrWhiteSpace(CustomerSearch));
        ChooseCustomerCommand = Command(() => FoundCustomer is { } c ? SetCustomerAsync(c.CustomerId) : Task.CompletedTask, () => _cartId.HasValue && FoundCustomer is not null);
        RemoveCustomerCommand = Command(() => SetCustomerAsync(null), () => _cartId.HasValue && HasCustomer);
        CheckoutCommand = Command(CheckoutAsync, () => CanCheckout);
        CloseSessionCommand = Command(CloseSessionAsync, () => HasOpenSession && Items.Count == 0);
    }

    public ObservableCollection<POSWarehouseResult> Warehouses { get; } = [];

    public ObservableCollection<POSCartItemResult> Items { get; } = [];

    public ICommand OpenSessionCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand CheckoutCommand { get; }
    public ICommand CloseSessionCommand { get; }
    public ICommand LineDiscountCommand { get; }
    public ICommand CartDiscountCommand { get; }
    public ICommand AddPaymentCommand { get; }
    public ICommand FindCustomerCommand { get; }
    public ICommand ChooseCustomerCommand { get; }
    public ICommand RemoveCustomerCommand { get; }

    /// <summary>Code, name or phone of the customer to find (FIX-11).</summary>
    public string CustomerSearch { get => _customerSearch; set => Set(ref _customerSearch, value); }

    /// <summary>Customers found when the search matched several (code and name only).</summary>
    public ObservableCollection<POSCustomerResult> FoundCustomers { get; } = [];

    public POSCustomerResult? FoundCustomer { get => _foundCustomer; set => Set(ref _foundCustomer, value); }

    public bool HasFoundCustomers => FoundCustomers.Count > 0;

    /// <summary>The customer of the sale in progress ("C-001 Jane Doe"), or null.</summary>
    public string? CustomerText { get => _customerText; private set { if (Set(ref _customerText, value)) Raise(nameof(HasCustomer)); } }

    public bool HasCustomer => CustomerText is not null;
    public ICommand RemovePaymentCommand { get; }

    /// <summary>Cash, card or other (FIX-10).</summary>
    public IReadOnlyList<PaymentChoice> PaymentMethods { get; }

    public PaymentChoice PaymentMethod { get => _paymentMethod; set { if (Set(ref _paymentMethod, value)) Raise(nameof(PaymentNeedsNote)); } }

    /// <summary>The amount of the next part as typed; for cash, what the customer hands over (more than due gives change).</summary>
    public string PaymentAmountText { get => _paymentAmountText; set => Set(ref _paymentAmountText, value); }

    /// <summary>A note for a card part (e.g. approval code), the description of an "Other" part (required).</summary>
    public string PaymentNote { get => _paymentNote; set => Set(ref _paymentNote, value); }

    public bool PaymentNeedsNote => PaymentMethod.Method == POSPaymentMethod.Other;

    /// <summary>The parts of a split payment taken so far for the cart on the till (kept on the screen until checkout).</summary>
    public ObservableCollection<PaymentPart> Payments { get; } = [];

    public bool HasPayments => Payments.Count > 0;

    /// <summary>What the parts pay of the total.</summary>
    public decimal AmountPaid => Payments.Sum(p => p.Amount);

    /// <summary>What is still to be paid (0 once the parts cover the total).</summary>
    public decimal AmountDue => Math.Max(0m, Total - AmountPaid);

    /// <summary>The change to hand back: cash handed over beyond what the cash part pays.</summary>
    public decimal ChangeDue => Payments.Sum(p => p.Change);

    /// <summary>Percentage or amount (FIX-08c).</summary>
    public IReadOnlyList<DiscountChoice> DiscountKinds { get; }

    public DiscountChoice DiscountKind { get => _discountKind; set => Set(ref _discountKind, value); }

    /// <summary>The discount as typed: 10 (%) or 2.50 (amount); 0 removes the discount.</summary>
    public string DiscountText { get => _discountText; set => Set(ref _discountText, value); }

    /// <summary>The line chosen in the cart (for a line discount).</summary>
    public POSCartItemResult? SelectedItem { get => _selectedItem; set => Set(ref _selectedItem, value); }

    /// <summary>True when the signed-in user may give discounts (pos.discount.give); the discount controls show only then.</summary>
    public bool CanGiveDiscounts { get => _canGiveDiscounts; private set => Set(ref _canGiveDiscounts, value); }

    /// <summary>All discounts in the cart: line discounts and the cart discount (FIX-08c).</summary>
    public decimal DiscountTotal { get => _discountTotal; private set => Set(ref _discountTotal, value); }

    public string CashierText => string.Format(CultureInfo.CurrentCulture, PosText.Cashier, _currentUser.DisplayName);

    public POSWarehouseResult? SelectedWarehouse
    {
        get => _selectedWarehouse;
        set => Set(ref _selectedWarehouse, value);
    }

    /// <summary>True when there is no active warehouse, so no till can be opened (the screen says what to do).</summary>
    public bool HasNoWarehouses
    {
        get => _hasNoWarehouses;
        private set => Set(ref _hasNoWarehouses, value);
    }

    public string ProductCode
    {
        get => _productCode;
        set => Set(ref _productCode, value);
    }

    /// <summary>The quantity as typed (parsed in the current culture when the product is added).</summary>
    public string QuantityText
    {
        get => _quantityText;
        set => Set(ref _quantityText, value);
    }

    public decimal Subtotal
    {
        get => _subtotal;
        private set => Set(ref _subtotal, value);
    }

    public decimal Total
    {
        get => _total;
        private set => Set(ref _total, value);
    }

    /// <summary>The tax contained in <see cref="Total"/> (prices include tax - FIX-08b).</summary>
    public decimal TaxTotal
    {
        get => _taxTotal;
        private set => Set(ref _taxTotal, value);
    }

    /// <summary>A peripheral problem after a completed sale (for example the receipt could not be printed). Not an error: the sale is valid.</summary>
    public string? HardwareMessage
    {
        get => _hardwareMessage;
        private set => Set(ref _hardwareMessage, value);
    }

    /// <summary>True while a barcode scanner is listening for this screen (the screen says so; without one the cashier types codes).</summary>
    public bool ScannerReady
    {
        get => _scannerReady;
        private set => Set(ref _scannerReady, value);
    }

    public bool HasOpenSession => _sessionId.HasValue;

    /// <summary>True while no till is open (the warehouse choice is shown).</summary>
    public bool NeedsSession => !HasOpenSession;

    /// <summary>A cart with items; with payment parts entered, only once they cover the total (no parts = the whole total in cash).</summary>
    public bool CanCheckout => _cartId.HasValue && Items.Count > 0 && !IsBusy && (Payments.Count == 0 || AmountDue == 0m);

    public Guid? SessionId => _sessionId;

    public Guid? CartId => _cartId;

    /// <summary>
    /// Each time the screen is shown: resume the cashier's open till (with its cart), or offer the warehouses to open one; then listen to
    /// the barcode scanner.
    /// </summary>
    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken);
        await ListenAsync(cancellationToken);
    }

    /// <summary>The screen is left (another screen, or sign-out): scans no longer reach this cart.</summary>
    public async Task OnNavigatedFromAsync(CancellationToken cancellationToken = default)
    {
        if (_scanner is null || !_listening) return;

        _listening = false;
        ScannerReady = false;
        _scanner.ScanProcessed -= OnScanProcessed;
        _scanner.BindCart(null);
        await _scanner.StopAsync(cancellationToken);
    }

    private Task LoadAsync(CancellationToken cancellationToken) => BusyAsync(async () =>
    {
        // FIX-08c: the discount controls show only to someone who may give discounts (the handler checks again)
        var mayDiscount = await _runner.QueryAsync(async (scope, ct) =>
            scope.Find<IAuthorizationService>() is { } authorization && await authorization.IsAllowedAsync(POS.Application.Security.POSCapabilities.GiveDiscounts, ct), cancellationToken);
        CanGiveDiscounts = mayDiscount.IsSuccess && mayDiscount.Value;

        if (HasOpenSession)
        {
            await RefreshCartAsync(cancellationToken);
            return;
        }

        var cashier = _currentUser.UserName;
        var resumed = await _runner.QueryAsync(async (scope, ct) =>
        {
            var reader = scope.Get<IPOSReader>();
            var session = await reader.FindOpenSessionAsync(cashier, ct);
            if (session is null)
                return new Resume(null, null, await reader.GetWarehousesAsync(ct), null);

            var cart = await reader.GetCurrentCartAsync(session.SessionId, ct);
            if (cart is not null)
                return new Resume(session.SessionId, cart, [], null);

            var started = await scope.Get<IPOSService>().StartCartAsync(session.SessionId, ct);
            return started.IsSuccess
                ? new Resume(session.SessionId, await reader.GetCartAsync(started.CartId, ct), [], null)
                : new Resume(session.SessionId, null, [], started.ErrorMessage ?? PosText.ActionFailed);
        }, cancellationToken);

        if (!Accept(resumed)) return;

        if (resumed.Value.SessionId is { } sessionId)
        {
            SetSession(sessionId, resumed.Value.Cart);
            if (resumed.Value.Error is { } error) Fail(error);
            else StatusMessage = PosText.SessionResumed;
            return;
        }

        ShowWarehouses(resumed.Value.Warehouses);
    });

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        if (_scanner is null || _listening) return;

        _ui = SynchronizationContext.Current;
        _scanner.ScanProcessed += OnScanProcessed;
        _listening = true;

        // No scanner configured, or it cannot start: nothing to tell the cashier beyond the missing "scanner ready" - codes can be typed.
        var started = await _scanner.StartAsync(cancellationToken);
        if (!started.IsSuccess)
        {
            await OnNavigatedFromAsync(cancellationToken);
            return;
        }

        ScannerReady = true;
        BindScanner();
    }

    /// <summary>Scans go to the cart on the screen, and nowhere while no cart is open or the screen is not shown.</summary>
    private void BindScanner() => _scanner?.BindCart(_listening ? _cartId : null);

    /// <summary>Raised by the scanner input once a scan has been added (or refused); shown on the UI thread.</summary>
    private void OnScanProcessed(object? sender, POSScanOutcome outcome)
    {
        if (_ui is { } ui && SynchronizationContext.Current != ui) ui.Post(_ => _ = ShowScanAsync(outcome), null);
        else _ = ShowScanAsync(outcome);
    }

    private async Task ShowScanAsync(POSScanOutcome outcome)
    {
        if (!_listening) return;
        if (!outcome.IsSuccess)
        {
            Fail(outcome.ErrorMessage);
            return;
        }

        ErrorMessage = null;
        await RefreshCartAsync(CancellationToken.None);
        if (ErrorMessage is null) StatusMessage = PosText.ItemAdded;
    }

    private static readonly POSPaymentRequest CashPayment = new(POSPaymentMethod.Cash);

    private sealed record Resume(Guid? SessionId, POSCartResult? Cart, IReadOnlyList<POSWarehouseResult> Warehouses, string? Error);

    private sealed record CartChange(bool IsSuccess, string? ErrorMessage, POSCartResult? Cart);

    private sealed record CheckoutOutcome(POSCheckoutResult Result, POSCartResult? Next);

    private sealed record CloseOutcome(bool IsSuccess, string? ErrorMessage, IReadOnlyList<POSWarehouseResult> Warehouses);

    private async Task OpenSessionAsync()
    {
        if (SelectedWarehouse is not { } warehouse) return;

        var cashier = _currentUser.UserName;
        var opened = await _runner.QueryAsync(async (scope, ct) =>
        {
            var service = scope.Get<IPOSService>();
            var session = await service.OpenSessionAsync(cashier, warehouse.WarehouseId, ct);
            if (!session.IsSuccess)
                return new Resume(null, null, [], session.ErrorMessage ?? PosText.ActionFailed);

            var cart = await service.StartCartAsync(session.SessionId, ct);
            return cart.IsSuccess
                ? new Resume(session.SessionId, await scope.Get<IPOSReader>().GetCartAsync(cart.CartId, ct), [], null)
                : new Resume(session.SessionId, null, [], cart.ErrorMessage ?? PosText.ActionFailed);
        });

        if (!Accept(opened)) return;
        if (opened.Value.SessionId is not { } sessionId)
        {
            Fail(opened.Value.Error);
            return;
        }

        SetSession(sessionId, opened.Value.Cart);
        if (opened.Value.Error is { } error) Fail(error);
        else StatusMessage = PosText.SessionOpened;
    }

    private async Task AddProductAsync()
    {
        if (_cartId is not { } cartId) return;
        if (!decimal.TryParse(QuantityText, NumberStyles.Number, CultureInfo.CurrentCulture, out var quantity) || quantity <= 0)
        {
            Fail(PosText.QuantityInvalid);
            return;
        }

        var code = ProductCode.Trim();
        var added = await _runner.QueryAsync(async (scope, ct) =>
        {
            var result = await scope.Get<IPOSService>().AddProductAsync(cartId, code, quantity, ct);
            return new CartChange(result.IsSuccess, result.ErrorMessage, await scope.Get<IPOSReader>().GetCartAsync(cartId, ct));
        });

        if (!Accept(added)) return;
        ShowCart(added.Value.Cart);
        if (!added.Value.IsSuccess)
        {
            Fail(added.Value.ErrorMessage);
            return;
        }

        ProductCode = string.Empty;
        QuantityText = "1";
        StatusMessage = PosText.ItemAdded;
    }

    private async Task RemoveProductAsync(Guid productId)
    {
        if (_cartId is not { } cartId) return;

        var removed = await _runner.QueryAsync(async (scope, ct) =>
        {
            var result = await scope.Get<IPOSService>().RemoveProductAsync(cartId, productId, ct);
            return new CartChange(result.IsSuccess, result.ErrorMessage, await scope.Get<IPOSReader>().GetCartAsync(cartId, ct));
        });

        if (!Accept(removed)) return;
        ShowCart(removed.Value.Cart);
        if (removed.Value.IsSuccess) StatusMessage = PosText.ItemRemoved;
        else Fail(removed.Value.ErrorMessage);
    }

    private async Task CheckoutAsync()
    {
        if (_cartId is not { } cartId || _sessionId is not { } sessionId) return;

        var total = Total;
        _scanner?.BindCart(null);   // a scan must not land in the cart that is being sold
        try
        {
            await SellAsync(cartId, sessionId, total);
        }
        finally
        {
            BindScanner();
        }
    }

    private async Task SellAsync(Guid cartId, Guid sessionId, decimal total)
    {
        var parts = Payments.Select(p => p.Request).ToList();
        var checkedOut = await _runner.QueryAsync(async (scope, ct) =>
        {
            var service = scope.Get<IPOSService>();
            // FIX-10: the parts entered on the screen; none = the whole total in cash (FIX-04)
            var result = parts.Count == 0
                ? await service.CheckoutAsync(cartId, payment: CashPayment, cancellationToken: ct)
                : await service.CheckoutWithPaymentsAsync(cartId, parts, cancellationToken: ct);
            if (!result.IsSuccess)
                return new CheckoutOutcome(result, null);

            // Ready for the next customer.
            var next = await service.StartCartAsync(sessionId, ct);
            return new CheckoutOutcome(result, next.IsSuccess ? await scope.Get<IPOSReader>().GetCartAsync(next.CartId, ct) : null);
        });

        if (!Accept(checkedOut)) return;
        var (sale, nextCart) = (checkedOut.Value.Result, checkedOut.Value.Next);
        if (!sale.IsSuccess)
        {
            Fail(sale.ErrorMessage);
            return;
        }

        // The sale is complete whatever the peripherals did; tell the cashier what must be done by hand.
        HardwareMessage = sale.HardwareNotices is { Count: > 0 } notices ? string.Join(" ", notices.Select(n => n.Message)) : null;
        _cartId = nextCart?.CartId;
        ClearPayments();
        ShowCart(nextCart);
        StatusMessage = sale.ChangeDue > 0m
            ? string.Format(CultureInfo.CurrentCulture, PosText.SaleCompletedWithChange, total, sale.ChangeDue)
            : string.Format(CultureInfo.CurrentCulture, PosText.SaleCompleted, total);
    }

    private async Task CloseSessionAsync()
    {
        if (_sessionId is not { } sessionId) return;

        _scanner?.BindCart(null);   // a scan must not refill the cart while the till closes
        try
        {
            await CloseAsync(sessionId);
        }
        finally
        {
            BindScanner();
        }
    }

    private async Task CloseAsync(Guid sessionId)
    {
        var closed = await _runner.QueryAsync(async (scope, ct) =>
        {
            var result = await scope.Get<IPOSService>().CloseSessionAsync(sessionId, ct);
            return new CloseOutcome(result.IsSuccess, result.ErrorMessage, result.IsSuccess ? await scope.Get<IPOSReader>().GetWarehousesAsync(ct) : []);
        });

        if (!Accept(closed)) return;
        if (!closed.Value.IsSuccess)
        {
            Fail(closed.Value.ErrorMessage);
            return;
        }

        _sessionId = null;
        _cartId = null;
        HardwareMessage = null;
        ShowCart(null);
        RaiseSessionChanged();
        ShowWarehouses(closed.Value.Warehouses);
        StatusMessage = PosText.SessionClosed;
    }

    private async Task RefreshCartAsync(CancellationToken cancellationToken)
    {
        if (_cartId is not { } cartId) return;
        var cart = await _runner.QueryAsync((scope, ct) => scope.Get<IPOSReader>().GetCartAsync(cartId, ct), cancellationToken);
        if (Accept(cart)) ShowCart(cart.Value);
    }

    private void SetSession(Guid sessionId, POSCartResult? cart)
    {
        _sessionId = sessionId;
        _cartId = cart?.CartId;
        Warehouses.Clear();
        HasNoWarehouses = false;
        ShowCart(cart);
        RaiseSessionChanged();
    }

    private void ShowWarehouses(IReadOnlyList<POSWarehouseResult> warehouses)
    {
        Warehouses.Clear();
        foreach (var warehouse in warehouses) Warehouses.Add(warehouse);
        HasNoWarehouses = Warehouses.Count == 0;
        SelectedWarehouse = Warehouses.Count == 1 ? Warehouses[0] : null;
        if (!HasNoWarehouses && StatusMessage is null) StatusMessage = PosText.ChooseWarehouse;
    }

    private void ShowCart(POSCartResult? cart)
    {
        Items.Clear();
        if (cart is not null)
            foreach (var item in cart.Items) Items.Add(item);

        Subtotal = cart?.Subtotal ?? 0m;
        var before = Total;
        Total = cart?.Total ?? 0m;
        if (Total != before && Payments.Count > 0)
        {
            // the parts were for the old total: take them again (nothing was recorded yet)
            ClearPayments();
            StatusMessage = PosText.PaymentsCleared;
        }
        TaxTotal = cart?.TaxTotal ?? 0m;
        DiscountTotal = cart?.DiscountTotal ?? 0m;
        CustomerText = cart?.CustomerId is null ? null : $"{cart.CustomerCode} {cart.CustomerName}";
        SelectedItem = SelectedItem is { } chosen ? Items.FirstOrDefault(i => i.ProductId == chosen.ProductId) : null;
        RaisePayments();
        BindScanner();
    }

    private async Task FindCustomerAsync()
    {
        var text = CustomerSearch.Trim();
        var found = await _runner.QueryAsync((scope, ct) => scope.Get<IPOSService>().FindCustomersAsync(text, ct));
        if (!Accept(found)) return;
        FoundCustomers.Clear();
        FoundCustomer = null;
        Raise(nameof(HasFoundCustomers));
        if (!found.Value.IsSuccess) { Fail(found.Value.ErrorMessage); return; }

        switch (found.Value.Customers.Count)
        {
            case 0:
                Fail(PosText.NoCustomerFound);
                break;
            case 1:
                await SetCustomerAsync(found.Value.Customers[0].CustomerId);   // the only match is attached at once
                break;
            default:
                foreach (var c in found.Value.Customers) FoundCustomers.Add(c);
                Raise(nameof(HasFoundCustomers));
                StatusMessage = PosText.ChooseCustomer;
                break;
        }
    }

    private async Task SetCustomerAsync(Guid? customerId)
    {
        if (_cartId is not { } cartId) return;
        var changed = await _runner.QueryAsync(async (scope, ct) =>
        {
            var set = await scope.Get<IPOSService>().SetCustomerAsync(cartId, customerId, ct);
            return new CartChange(set.IsSuccess, set.ErrorMessage, await scope.Get<IPOSReader>().GetCartAsync(cartId, ct));
        });
        if (!Accept(changed)) return;

        ShowCart(changed.Value.Cart);
        if (!changed.Value.IsSuccess) { Fail(changed.Value.ErrorMessage); return; }

        FoundCustomers.Clear();
        (FoundCustomer, CustomerSearch, ErrorMessage) = (null, string.Empty, null);
        Raise(nameof(HasFoundCustomers));
        StatusMessage = customerId is null ? PosText.CustomerRemoved : string.Format(CultureInfo.CurrentCulture, PosText.CustomerChosen, CustomerText);
    }

    private Task AddPaymentAsync()
    {
        if (!decimal.TryParse(PaymentAmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) || amount <= 0m || decimal.Round(amount, 2) != amount)
        {
            Fail(PosText.PaymentAmountInvalid);
            return Task.CompletedTask;
        }

        var method = PaymentMethod.Method;
        var note = string.IsNullOrWhiteSpace(PaymentNote) ? null : PaymentNote.Trim();
        var due = AmountDue;
        if (method == POSPaymentMethod.Other && note is null)
        {
            Fail(PosText.PaymentNoteRequired);
            return Task.CompletedTask;
        }

        if (method != POSPaymentMethod.Cash && amount > due)
        {
            Fail(string.Format(CultureInfo.CurrentCulture, PosText.PaymentMoreThanDue, due));
            return Task.CompletedTask;
        }

        // cash may be more than is due: the part pays what is due and the rest is the change
        var part = method == POSPaymentMethod.Cash
            ? new POSPaymentRequest(method, TenderedAmount: amount, MethodDetail: note, Amount: Math.Min(amount, due))
            : new POSPaymentRequest(method, MethodDetail: note, Amount: amount);
        Payments.Add(PaymentPart.From(part, PaymentMethod.Text));
        (PaymentNote, ErrorMessage) = (string.Empty, null);
        RaisePayments();
        PaymentAmountText = AmountDue > 0m ? AmountDue.ToString("0.00", CultureInfo.CurrentCulture) : string.Empty;
        StatusMessage = AmountDue > 0m
            ? string.Format(CultureInfo.CurrentCulture, PosText.PaymentAdded, AmountDue)
            : ChangeDue > 0m ? string.Format(CultureInfo.CurrentCulture, PosText.PaymentCoveredWithChange, ChangeDue) : PosText.PaymentCovered;
        return Task.CompletedTask;
    }

    private void RemovePayment(PaymentPart part)
    {
        Payments.Remove(part);
        RaisePayments();
        PaymentAmountText = AmountDue.ToString("0.00", CultureInfo.CurrentCulture);
    }

    private void ClearPayments()
    {
        Payments.Clear();
        (PaymentAmountText, PaymentNote) = (string.Empty, string.Empty);
        RaisePayments();
    }

    private void RaisePayments()
    {
        foreach (var name in new[] { nameof(HasPayments), nameof(AmountPaid), nameof(AmountDue), nameof(ChangeDue), nameof(CanCheckout) }) Raise(name);
    }

    private Task GiveLineDiscountAsync()
        => SelectedItem is { } line ? GiveDiscountAsync((service, cartId, kind, value, ct) => service.SetLineDiscountAsync(cartId, line.ProductId, kind, value, ct)) : Task.CompletedTask;

    private Task GiveCartDiscountAsync()
        => GiveDiscountAsync((service, cartId, kind, value, ct) => service.SetCartDiscountAsync(cartId, kind, value, ct));

    private async Task GiveDiscountAsync(Func<IPOSService, Guid, POSDiscountKind, decimal, CancellationToken, Task<POSOperationResult>> give)
    {
        if (_cartId is not { } cartId) return;
        if (!decimal.TryParse(DiscountText, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || value < 0m)
        {
            Fail(PosText.DiscountInvalid);
            return;
        }

        var kind = DiscountKind.Kind;
        var given = await _runner.QueryAsync(async (scope, ct) =>
        {
            var result = await give(scope.Get<IPOSService>(), cartId, kind, value, ct);
            return new CartChange(result.IsSuccess, result.ErrorMessage, await scope.Get<IPOSReader>().GetCartAsync(cartId, ct));
        });

        if (!Accept(given)) return;
        ShowCart(given.Value.Cart);
        if (!given.Value.IsSuccess)
        {
            Fail(given.Value.ErrorMessage);
            return;
        }

        DiscountText = string.Empty;
        StatusMessage = value == 0m ? PosText.DiscountRemoved : PosText.DiscountGiven;
    }

    private void RaiseSessionChanged()
    {
        Raise(nameof(HasOpenSession));
        Raise(nameof(NeedsSession));
        Raise(nameof(SessionId));
        Raise(nameof(CanCheckout));
    }

    private void Fail(string? message)
    {
        ErrorMessage = string.IsNullOrWhiteSpace(message) ? PosText.ActionFailed : message;
        StatusMessage = null;
    }
}

/// <summary>A payment method the till offers (FIX-10).</summary>
public sealed record PaymentChoice(POSPaymentMethod Method, string Text);

/// <summary>One part of a split payment as the till shows it: what it pays and, for cash, what was handed over and the change.</summary>
public sealed record PaymentPart(POSPaymentRequest Request, string Text)
{
    public decimal Amount => Request.Amount ?? 0m;
    public decimal Change => Request.TenderedAmount is { } tendered ? Math.Max(0m, tendered - Amount) : 0m;

    public static PaymentPart From(POSPaymentRequest request, string methodName)
    {
        var amount = (request.Amount ?? 0m).ToString("N2", CultureInfo.CurrentCulture);
        var text = request.Method == POSPaymentMethod.Cash && request.TenderedAmount is { } tendered && tendered > request.Amount
            ? string.Format(CultureInfo.CurrentCulture, PosText.PaymentPartCash, methodName, amount, tendered.ToString("N2", CultureInfo.CurrentCulture),
                (tendered - request.Amount!.Value).ToString("N2", CultureInfo.CurrentCulture))
            : string.Format(CultureInfo.CurrentCulture, PosText.PaymentPart, methodName, amount);
        return new(request, request.MethodDetail is { } note ? $"{text} ({note})" : text);
    }
}
