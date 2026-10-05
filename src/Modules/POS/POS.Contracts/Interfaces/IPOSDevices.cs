using POS.Contracts.Models;

namespace POS.Contracts.Interfaces;

/// <summary>
/// The cashier's access to the OPTIONAL peripherals: receipt printer, label printer, cash drawer and scale.
///
/// Hardware is optional and never the source of truth: every operation reports a hardware problem as a failed result (it never
/// throws), a missing device is just "not configured", and nothing here can change a sale, stock or payment. Receipts and the
/// drawer are also driven automatically by checkout (see <see cref="POSCheckoutResult.HardwareNotices"/>).
/// </summary>
public interface IPOSDevices
{
    /// <summary>(Re)prints the receipt of a cart that has been checked out.</summary>
    Task<POSOperationResult> PrintReceiptAsync(Guid cartId, CancellationToken cancellationToken = default);

    /// <summary>Opens the cash drawer ("no sale").</summary>
    Task<POSOperationResult> OpenCashDrawerAsync(CancellationToken cancellationToken = default);

    /// <summary>Prints a shelf/product label for the product with this barcode or SKU.</summary>
    Task<POSOperationResult> PrintProductLabelAsync(string productCode, int copies = 1, CancellationToken cancellationToken = default);

    /// <summary>Reads the scale. An unreadable or implausible reading is a failure.</summary>
    Task<POSWeightResult> ReadWeightAsync(CancellationToken cancellationToken = default);

    /// <summary>The state of every peripheral kind (scanner, receipt printer, label printer, cash drawer, scale).</summary>
    Task<IReadOnlyList<POSDeviceStatusResult>> GetDeviceStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Connects the (optional) barcode scanner to the cart: every scan is added to the bound cart exactly as a typed code would be
/// (<see cref="IPOSService.AddProductAsync"/>). The scanner only supplies input; scanner trouble never changes cart, sale or stock.
/// </summary>
public interface IPOSBarcodeInput
{
    /// <summary>Raised after each scan has been processed (added, or rejected with a reason).</summary>
    event EventHandler<POSScanOutcome>? ScanProcessed;

    /// <summary>Sets the cart that receives scans, or null when no cart is open (scans are then reported as rejected).</summary>
    void BindCart(Guid? cartId);

    /// <summary>Starts listening. Fails when there is no scanner; the cashier can still type codes.</summary>
    Task<POSOperationResult> StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
