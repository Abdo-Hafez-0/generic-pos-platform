using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;

namespace Client.Hardware.Transport;

/// <summary>
/// How bytes reach a printer or drawer. Protocol adapters (ESC/POS, ZPL) are written against this, so the same adapter works
/// over the network or a device path. Implementations never throw for device problems: they return a failed result.
/// </summary>
public interface IDeviceTransport
{
    string Description { get; }

    Task<Result> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    Task<DeviceStatus> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Raw TCP (for example a network receipt printer on the shop LAN). Local network only: no internet is involved.</summary>
public sealed class TcpDeviceTransport(string host, int port, TimeSpan timeout, ILogger? logger = null) : IDeviceTransport
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Description => $"tcp://{host}:{port}";

    public async Task<Result> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeoutSource.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(data, timeoutSource.Token);
            await stream.FlushAsync(timeoutSource.Token);
            return Result.Success();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Device {Device} timed out after {Timeout}.", Description, timeout);
            return Result.Failure(HardwareErrors.Timeout(Description));
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or ArgumentException)
        {
            _logger.LogWarning(ex, "Device {Device} is not reachable.", Description);
            return Result.Failure(HardwareErrors.Unavailable(Description, ex.Message));
        }
    }

    public async Task<DeviceStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeoutSource.Token);
            return DeviceStatus.Ready();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DeviceStatus.Unavailable($"{Description} did not respond in time.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or ArgumentException)
        {
            return DeviceStatus.Unavailable($"{Description}: {ex.Message}");
        }
    }
}

/// <summary>A device path: a shared printer (UNC) or a device node. The path must already exist; it is never created.</summary>
public sealed class FileDeviceTransport(string path, TimeSpan timeout, ILogger? logger = null) : IDeviceTransport
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Description => $"file://{path}";

    public async Task<Result> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        try
        {
            // Opening a disconnected device can block, so the whole write runs off-thread with a timeout.
            await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(data.Span);
                stream.Flush();
            }, CancellationToken.None).WaitAsync(timeout, cancellationToken);

            return Result.Success();
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Device {Device} timed out after {Timeout}.", Description, timeout);
            return Result.Failure(HardwareErrors.Timeout(Description));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger.LogWarning(ex, "Device {Device} is not available.", Description);
            return Result.Failure(HardwareErrors.Unavailable(Description, ex.Message));
        }
    }

    public async Task<DeviceStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }, CancellationToken.None).WaitAsync(timeout, cancellationToken);

            return DeviceStatus.Ready();
        }
        catch (TimeoutException)
        {
            return DeviceStatus.Unavailable($"{Description} did not respond in time.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return DeviceStatus.Unavailable($"{Description}: {ex.Message}");
        }
    }
}
