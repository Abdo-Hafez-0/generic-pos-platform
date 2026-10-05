using System.Net;
using System.Net.Sockets;
using Client.Hardware.Transport;
using Platform.Application.Abstractions.Hardware;

namespace Hardware.Tests;

public sealed class TcpTransportTests
{
    private static (TcpListener Listener, int Port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    private static async Task<byte[]> ReceiveAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var buffer = new MemoryStream();
        await client.GetStream().CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task Bytes_ReachAListeningDevice_Exactly()
    {
        var (listener, port) = Listen();
        try
        {
            var received = ReceiveAsync(listener);
            var transport = new TcpDeviceTransport("127.0.0.1", port, TimeSpan.FromSeconds(3));
            var payload = new byte[] { 0x1B, 0x40, 1, 2, 3, 0xFF };

            var result = await transport.SendAsync(payload);

            Assert.True(result.IsSuccess);
            Assert.Equal(payload, await received.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ADeviceThatIsOff_IsUnavailable_NotAnException()
    {
        var (listener, port) = Listen();
        listener.Stop();                                       // nothing listens on this port any more
        var transport = new TcpDeviceTransport("127.0.0.1", port, TimeSpan.FromSeconds(15));

        var result = await transport.SendAsync(new byte[] { 1 });

        Assert.True(result.IsFailure);
        Assert.Equal(HardwareErrors.UnavailableCode, result.Error.Code);
        Assert.Contains(transport.Description, result.Error.Description);
    }

    [Fact]
    public async Task AnUnresolvableHost_IsUnavailable_NotAnException()
    {
        var transport = new TcpDeviceTransport("no-such-host.invalid", 9100, TimeSpan.FromSeconds(3));

        Assert.Equal(HardwareErrors.UnavailableCode, (await transport.SendAsync(new byte[] { 1 })).Error.Code);
    }

    [Fact]
    public async Task AnEmptyHost_IsUnavailable_NotAnException()
    {
        var transport = new TcpDeviceTransport("", 9100, TimeSpan.FromSeconds(1));

        Assert.True((await transport.SendAsync(new byte[] { 1 })).IsFailure);
        Assert.Equal(DeviceState.Unavailable, (await transport.ProbeAsync()).State);
    }

    [Fact]
    public async Task Probe_ReflectsWhetherTheDeviceAnswers()
    {
        var (listener, port) = Listen();
        var transport = new TcpDeviceTransport("127.0.0.1", port, TimeSpan.FromSeconds(2));

        Assert.True((await transport.ProbeAsync()).IsReady);

        listener.Stop();
        var down = await transport.ProbeAsync();
        Assert.Equal(DeviceState.Unavailable, down.State);
        Assert.False(string.IsNullOrEmpty(down.Message));
    }

    [Fact]
    public async Task ACallerWhoCancels_GetsACancellation_NotAHardwareFailure()
    {
        var (listener, port) = Listen();
        try
        {
            var transport = new TcpDeviceTransport("127.0.0.1", port, TimeSpan.FromSeconds(3));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(new byte[] { 1 }, cts.Token));
        }
        finally
        {
            listener.Stop();
        }
    }
}

public sealed class FileTransportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hardware-tests-" + Guid.NewGuid().ToString("N"));

    public FileTransportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    private string Device(byte[]? initial = null)
    {
        var path = Path.Combine(_dir, "printer.dev");
        File.WriteAllBytes(path, initial ?? []);
        return path;
    }

    [Fact]
    public async Task Bytes_AreWrittenToTheDevicePath()
    {
        var path = Device();
        var transport = new FileDeviceTransport(path, TimeSpan.FromSeconds(3));

        var result = await transport.SendAsync(new byte[] { 0x1B, 0x70, 0, 50, 250 });

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x1B, 0x70, 0, 50, 250 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task AMissingDevice_IsUnavailable_AndIsNeverCreated()
    {
        var path = Path.Combine(_dir, "not-there.dev");
        var transport = new FileDeviceTransport(path, TimeSpan.FromSeconds(1));

        var result = await transport.SendAsync(new byte[] { 1 });

        Assert.Equal(HardwareErrors.UnavailableCode, result.Error.Code);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0path")]
    public async Task AnInvalidPath_IsUnavailable_NotAnException(string path)
    {
        var transport = new FileDeviceTransport(path, TimeSpan.FromSeconds(1));

        Assert.Equal(HardwareErrors.UnavailableCode, (await transport.SendAsync(new byte[] { 1 })).Error.Code);
        Assert.Equal(DeviceState.Unavailable, (await transport.ProbeAsync()).State);
    }

    [Fact]
    public async Task Probe_ReflectsWhetherTheDevicePathExists()
    {
        var path = Device();
        var transport = new FileDeviceTransport(path, TimeSpan.FromSeconds(1));

        Assert.True((await transport.ProbeAsync()).IsReady);
        File.Delete(path);
        Assert.Equal(DeviceState.Unavailable, (await transport.ProbeAsync()).State);
    }

    [Fact]
    public async Task ADeviceThatIsBusy_StillAllowsTheWrite_BecauseSharingIsAllowed()
    {
        var path = Device();
        await using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var transport = new FileDeviceTransport(path, TimeSpan.FromSeconds(1));

        Assert.True((await transport.SendAsync(new byte[] { 7 })).IsSuccess);
    }
}
