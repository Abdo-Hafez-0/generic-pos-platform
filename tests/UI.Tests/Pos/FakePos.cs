using POS.Contracts.Interfaces;
using POS.Contracts.Models;

namespace UI.Tests.Pos;

/// <summary>
/// An in-memory till behind IPOSService/IPOSReader for view-model tests. The STATE is shared (like a database); the service objects are
/// created per DI scope, so <see cref="Instances"/> shows how many scopes (user actions) were used.
/// </summary>
internal sealed class FakeTill
{
    public List<POSWarehouseResult> Warehouses { get; } = [];
    public Dictionary<string, (Guid ProductId, string Name, decimal Price)> Products { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Guid, POSSessionResult> Sessions { get; } = [];
    public Dictionary<Guid, List<POSCartItemResult>> Carts { get; } = [];
    public Dictionary<Guid, Guid> CartSession { get; } = [];
    public HashSet<Guid> CheckedOut { get; } = [];
    public List<string> Calls { get; } = [];
    public int Instances { get; set; }
    public Exception? Throw { get; set; }
    public string? CheckoutRefusal { get; set; }
    public string? DiscountRefusal { get; set; }
    public List<(Guid? ProductId, POSDiscountKind Kind, decimal Value)> Discounts { get; } = [];
    public IReadOnlyList<POSHardwareNotice>? HardwareNotices { get; set; }
    public POSPaymentRequest? LastPayment { get; set; }
    public IReadOnlyList<POSPaymentRequest>? LastPayments { get; set; }
    public decimal ChangeDue { get; set; }
    public List<POSCustomerResult> Customers { get; } = [];
    public Dictionary<Guid, POSCustomerResult> CartCustomer { get; } = [];

    public Guid AddWarehouse(string name)
    {
        var id = Guid.NewGuid();
        Warehouses.Add(new POSWarehouseResult(id, name.ToUpperInvariant(), name));
        return id;
    }

    public Guid OpenSessionFor(string cashier, Guid warehouseId)
    {
        var id = Guid.NewGuid();
        Sessions[id] = new POSSessionResult(id, cashier, warehouseId, POSSessionStatusContract.Open, DateTime.UtcNow, null);
        return id;
    }

    public Guid StartCart(Guid sessionId)
    {
        var id = Guid.NewGuid();
        Carts[id] = [];
        CartSession[id] = sessionId;
        return id;
    }

    public POSCartResult? Cart(Guid cartId)
    {
        if (!Carts.TryGetValue(cartId, out var items)) return null;
        var total = items.Sum(i => i.LineTotal);
        return new POSCartResult(cartId, CartSession[cartId], CheckedOut.Contains(cartId) ? POSCartStatusContract.CheckedOut : POSCartStatusContract.Open,
            items.ToList(), total, total, null, DateTime.UtcNow, null,
            CustomerId: CartCustomer.GetValueOrDefault(cartId)?.CustomerId, CustomerCode: CartCustomer.GetValueOrDefault(cartId)?.Code,
            CustomerName: CartCustomer.GetValueOrDefault(cartId)?.Name);
    }
}

internal sealed class FakePosService : IPOSService, IPOSReader
{
    private readonly FakeTill _till;

    public FakePosService(FakeTill till)
    {
        _till = till;
        till.Instances++;
    }

    private void Enter(string call)
    {
        _till.Calls.Add(call);
        if (_till.Throw is { } failure) throw failure;
    }

    public Task<POSOpenSessionResult> OpenSessionAsync(string cashierReference, Guid warehouseId, CancellationToken cancellationToken = default)
    {
        Enter($"open:{cashierReference}:{warehouseId}");
        return Task.FromResult(POSOpenSessionResult.Success(_till.OpenSessionFor(cashierReference, warehouseId)));
    }

    public Task<POSOperationResult> CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Enter("close");
        var openCart = _till.CartSession.Where(c => c.Value == sessionId && !_till.CheckedOut.Contains(c.Key)).Select(c => c.Key).FirstOrDefault();
        if (openCart != Guid.Empty && _till.Carts[openCart].Count > 0)
            return Task.FromResult(POSOperationResult.Failure("POS.CloseSession.OpenCartHasItems", "The open cart still has items."));

        var session = _till.Sessions[sessionId];
        _till.Sessions[sessionId] = session with { Status = POSSessionStatusContract.Closed };
        return Task.FromResult(POSOperationResult.Success());
    }

    public Task<POSStartCartResult> StartCartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        Enter("start-cart");
        return Task.FromResult(POSStartCartResult.Success(_till.StartCart(sessionId)));
    }

    public Task<POSAddItemResult> AddProductAsync(Guid cartId, string productCode, decimal quantity = 1, CancellationToken cancellationToken = default)
    {
        Enter($"add:{productCode}:{quantity}");
        if (!_till.Products.TryGetValue(productCode, out var product))
            return Task.FromResult(POSAddItemResult.Failure("POS.AddItem.ProductNotFound", $"No product with code '{productCode}'."));

        var item = new POSCartItemResult(Guid.NewGuid(), product.ProductId, productCode, product.Name, quantity, product.Price, product.Price * quantity);
        _till.Carts[cartId].Add(item);
        return Task.FromResult(POSAddItemResult.Success(item.ItemId));
    }

    public Task<POSOperationResult> RemoveProductAsync(Guid cartId, Guid productId, CancellationToken cancellationToken = default)
    {
        Enter("remove");
        _till.Carts[cartId].RemoveAll(i => i.ProductId == productId);
        return Task.FromResult(POSOperationResult.Success());
    }

    public Task<POSOperationResult> ChangeQuantityAsync(Guid cartId, Guid productId, decimal quantity, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<POSOperationResult> ClearCartAsync(Guid cartId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<POSOperationResult> SetLineDiscountAsync(Guid cartId, Guid productId, POSDiscountKind kind, decimal value, CancellationToken cancellationToken = default)
    {
        Enter($"line-discount:{kind}:{value}");
        if (_till.DiscountRefusal is { } refusal) return Task.FromResult(POSOperationResult.Failure("POS.Discount.Refused", refusal));
        _till.Discounts.Add((productId, kind, value));
        return Task.FromResult(POSOperationResult.Success());
    }

    public Task<POSOperationResult> SetCartDiscountAsync(Guid cartId, POSDiscountKind kind, decimal value, CancellationToken cancellationToken = default)
    {
        Enter($"cart-discount:{kind}:{value}");
        if (_till.DiscountRefusal is { } refusal) return Task.FromResult(POSOperationResult.Failure("POS.Discount.Refused", refusal));
        _till.Discounts.Add((null, kind, value));
        return Task.FromResult(POSOperationResult.Success());
    }

    public Task<POSCheckoutResult> CheckoutAsync(Guid cartId, string? transactionReference = null, POSPaymentRequest? payment = null, CancellationToken cancellationToken = default)
    {
        Enter("checkout");
        _till.LastPayment = payment;
        if (_till.CheckoutRefusal is { } refusal)
            return Task.FromResult(POSCheckoutResult.Failure("POS.Checkout.Refused", refusal));

        _till.CheckedOut.Add(cartId);
        return Task.FromResult(POSCheckoutResult.Success(Guid.NewGuid(), hardwareNotices: _till.HardwareNotices));
    }

    public Task<POSCustomerSearchResult> FindCustomersAsync(string text, CancellationToken cancellationToken = default)
    {
        Enter("find customers");
        return Task.FromResult(POSCustomerSearchResult.Success(_till.Customers
            .Where(c => c.Code.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList()));
    }

    public Task<POSOperationResult> SetCustomerAsync(Guid cartId, Guid? customerId, CancellationToken cancellationToken = default)
    {
        Enter("set customer");
        if (customerId is { } id) _till.CartCustomer[cartId] = _till.Customers.Single(c => c.CustomerId == id);
        else _till.CartCustomer.Remove(cartId);
        return Task.FromResult(POSOperationResult.Success());
    }

    public Task<POSCheckoutResult> CheckoutWithPaymentsAsync(Guid cartId, IReadOnlyList<POSPaymentRequest> payments, string? transactionReference = null, CancellationToken cancellationToken = default)
    {
        Enter("checkout");
        _till.LastPayments = payments;
        if (_till.CheckoutRefusal is { } refusal)
            return Task.FromResult(POSCheckoutResult.Failure("POS.Checkout.Refused", refusal));

        _till.CheckedOut.Add(cartId);
        return Task.FromResult(POSCheckoutResult.Success(Guid.NewGuid(), changeDue: _till.ChangeDue, hardwareNotices: _till.HardwareNotices));
    }

    public Task<POSSessionResult?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(_till.Sessions.GetValueOrDefault(sessionId));

    public Task<POSCartResult?> GetCartAsync(Guid cartId, CancellationToken cancellationToken = default)
        => Task.FromResult(_till.Cart(cartId));

    public Task<POSCartResult?> GetCurrentCartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var cartId = _till.CartSession.Where(c => c.Value == sessionId && !_till.CheckedOut.Contains(c.Key)).Select(c => c.Key).LastOrDefault();
        return Task.FromResult(cartId == Guid.Empty ? null : _till.Cart(cartId));
    }

    public Task<POSSessionResult?> FindOpenSessionAsync(string cashierReference, CancellationToken cancellationToken = default)
    {
        Enter("find-open");
        return Task.FromResult(_till.Sessions.Values.LastOrDefault(s => s.CashierReference == cashierReference && s.Status == POSSessionStatusContract.Open));
    }

    public Task<IReadOnlyList<POSWarehouseResult>> GetWarehousesAsync(CancellationToken cancellationToken = default)
    {
        Enter("warehouses");
        return Task.FromResult<IReadOnlyList<POSWarehouseResult>>(_till.Warehouses.ToList());
    }
}

/// <summary>
/// The scanner side of the till for view-model tests (FIX-02): <see cref="Scan"/> does what POSBarcodeInput does - add the code to the BOUND
/// cart, or refuse it - and raises <see cref="ScanProcessed"/>.
/// </summary>
internal sealed class FakeScannerInput(FakeTill till) : IPOSBarcodeInput
{
    public event EventHandler<POSScanOutcome>? ScanProcessed;

    public bool CanStart { get; set; } = true;
    public bool Started { get; private set; }
    public Guid? BoundCart { get; private set; }
    public List<Guid?> Bindings { get; } = [];
    public int Subscribers => ScanProcessed?.GetInvocationList().Length ?? 0;

    public void BindCart(Guid? cartId)
    {
        BoundCart = cartId;
        Bindings.Add(cartId);
    }

    public Task<POSOperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        Started = CanStart;
        return Task.FromResult(CanStart ? POSOperationResult.Success() : POSOperationResult.Failure("Hardware.NotConfigured", "No barcode scanner is configured."));
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Started = false;
        return Task.CompletedTask;
    }

    public void Scan(string code)
    {
        POSScanOutcome outcome;
        if (BoundCart is not { } cartId)
            outcome = new POSScanOutcome(code, false, null, "POS.Scan.NoActiveCart", "Open a cart before scanning.");
        else if (!till.Products.TryGetValue(code, out var product))
            outcome = new POSScanOutcome(code, false, null, "POS.AddItem.ProductNotFound", $"No product with code '{code}'.");
        else
        {
            var item = new POSCartItemResult(Guid.NewGuid(), product.ProductId, code, product.Name, 1m, product.Price, product.Price);
            till.Carts[cartId].Add(item);
            outcome = new POSScanOutcome(code, true, item.ItemId, null, null);
        }

        ScanProcessed?.Invoke(this, outcome);
    }
}
