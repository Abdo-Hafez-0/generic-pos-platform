using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Hardware;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;

namespace POS.Infrastructure.Services;

/// <summary>
/// Feeds scanned barcodes into the bound cart through IPOSService.AddProductAsync - the same path as a typed code - so the scanner
/// is pure input: validation, stock checks and persistence stay in POS. Scans are processed one at a time and any failure
/// (including an exception) becomes a rejected outcome; nothing here can break the scanner, the cart or a sale.
/// </summary>
internal sealed class POSBarcodeInput(
    IServiceScopeFactory scopeFactory,
    ILogger<POSBarcodeInput>? logger = null,
    IBarcodeScanner? scanner = null) : IPOSBarcodeInput
{
    private readonly ILogger _logger = logger ?? NullLogger<POSBarcodeInput>.Instance;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly object _gate = new();
    private Guid? _cartId;
    private bool _subscribed;

    public event EventHandler<POSScanOutcome>? ScanProcessed;

    public void BindCart(Guid? cartId)
    {
        lock (_gate) _cartId = cartId;
    }

    public async Task<POSOperationResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (scanner is null)
            return POSOperationResult.Failure(HardwareErrors.NotConfiguredCode, "No barcode scanner is configured.");

        lock (_gate)
        {
            if (!_subscribed)
            {
                scanner.BarcodeScanned += OnScanned;
                _subscribed = true;
            }
        }

        var started = await HardwareGuard.RunAsync("barcode scanner", () => scanner.StartAsync(cancellationToken));
        if (started.IsFailure)
        {
            _logger.LogWarning("The barcode scanner could not be started: {Error}", started.Error);
            return POSOperationResult.Failure(started.Error.Code, started.Error.Description);
        }

        return POSOperationResult.Success();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (scanner is null) return;

        lock (_gate)
        {
            if (_subscribed)
            {
                scanner.BarcodeScanned -= OnScanned;
                _subscribed = false;
            }
        }

        try
        {
            await scanner.StopAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The barcode scanner did not stop cleanly.");
        }
    }

    private void OnScanned(object? sender, BarcodeScannedEventArgs e) => _ = ProcessAsync(e.Scan.Code);

    /// <summary>Adds one scanned code to the bound cart and reports the outcome. Never throws.</summary>
    internal async Task ProcessAsync(string code)
    {
        POSScanOutcome outcome;

        await _one.WaitAsync();
        try
        {
            Guid? cartId;
            lock (_gate) cartId = _cartId;

            if (cartId is null)
            {
                outcome = new POSScanOutcome(code, false, null, "POS.Scan.NoActiveCart", "Open a cart before scanning.");
            }
            else
            {
                using var scope = scopeFactory.CreateScope();
                var added = await scope.ServiceProvider.GetRequiredService<IPOSService>().AddProductAsync(cartId.Value, code, 1m);
                outcome = new POSScanOutcome(code, added.IsSuccess, added.IsSuccess ? added.ItemId : null, added.ErrorCode, added.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing scanned code {Code} failed.", code);
            outcome = new POSScanOutcome(code, false, null, "POS.Scan.Failed", "The scan could not be processed. Type the code instead.");
        }
        finally
        {
            _one.Release();
        }

        // One handler at a time: a faulty subscriber must not stop the others (a multicast Invoke would).
        foreach (var handler in ScanProcessed?.GetInvocationList().Cast<EventHandler<POSScanOutcome>>() ?? [])
        {
            try
            {
                handler(this, outcome);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A scan-outcome subscriber threw.");
            }
        }
    }
}
