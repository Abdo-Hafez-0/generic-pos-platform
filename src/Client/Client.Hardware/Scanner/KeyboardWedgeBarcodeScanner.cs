using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;

namespace Client.Hardware.Scanner;

/// <summary>
/// Where keyboard characters are delivered to a keyboard-wedge scanner. The UI forwards the characters it receives (it does not
/// decode anything and does not know the scanner): the decoding lives here, in the adapter.
/// </summary>
public interface IKeyboardInputSink
{
    /// <summary>Delivers one typed character. Carriage return or line feed ends a scan.</summary>
    void OnCharacter(char character);
}

/// <summary>
/// Most POS scanners behave as a keyboard: they "type" the code very fast and finish with Enter. This adapter recognises that
/// pattern (characters arriving close together, ended by Enter, long enough) and raises <see cref="BarcodeScanned"/>. Slow typing
/// by a person is not mistaken for a scan. No vendor SDK is needed. A scanner is input only: nothing here touches business data.
/// </summary>
public sealed class KeyboardWedgeBarcodeScanner(
    int minimumLength = 4,
    int maxInterCharacterMilliseconds = 80,
    TimeProvider? timeProvider = null,
    ILogger? logger = null) : IBarcodeScanner, IKeyboardInputSink
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly object _gate = new();
    private readonly StringBuilder _buffer = new();
    private DateTimeOffset _lastCharacterAt;
    private bool _started;

    public event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(DeviceStatus.Ready());

    public Task<Result> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { _started = true; _buffer.Clear(); }
        return Task.FromResult(Result.Success());
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { _started = false; _buffer.Clear(); }
        return Task.CompletedTask;
    }

    public void OnCharacter(char character)
    {
        string? code = null;
        DateTimeOffset now;

        lock (_gate)
        {
            if (!_started) return;

            now = _time.GetUtcNow();
            if (_buffer.Length > 0 && (now - _lastCharacterAt).TotalMilliseconds > maxInterCharacterMilliseconds)
                _buffer.Clear();                                    // too slow to be a scanner: that was typing

            if (character is '\r' or '\n')
            {
                if (_buffer.Length >= minimumLength) code = _buffer.ToString();
                _buffer.Clear();
            }
            else if (!char.IsControl(character))
            {
                _buffer.Append(character);
                _lastCharacterAt = now;
            }
        }

        if (code is not null)
            Raise(new BarcodeScan(code, now));
    }

    private void Raise(BarcodeScan scan)
    {
        var handlers = BarcodeScanned;
        if (handlers is null) return;

        var args = new BarcodeScannedEventArgs(scan);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BarcodeScannedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                // A faulty subscriber must never stop the scanner or corrupt anything: report and carry on.
                _logger.LogError(ex, "A barcode subscriber threw while handling a scan.");
            }
        }
    }
}
