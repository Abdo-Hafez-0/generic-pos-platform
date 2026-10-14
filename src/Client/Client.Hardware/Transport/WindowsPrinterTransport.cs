using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;

namespace Client.Hardware.Transport;

/// <summary>
/// MISS-07a: a printer installed in Windows (usually USB with the maker's driver) reached through the Windows print queue. The bytes the
/// ESC/POS or ZPL adapter produced go to the queue "RAW", so the driver passes them to the printer unchanged. Before anything is sent the
/// queue's state is read: a printer that is offline, out of paper, jammed, open, paused or set to "Use Printer Offline" is reported in
/// plain words and nothing is queued (a job left waiting would print later, unexpectedly). Windows only; never throws for device problems.
/// </summary>
public sealed class WindowsPrinterTransport(string printerName, TimeSpan timeout, ILogger? logger = null) : IDeviceTransport
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public string Description => $"Windows printer '{printerName}'";

    public async Task<Result> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return Result.Failure(HardwareErrors.Unavailable(Description, "Windows printers can only be used on Windows."));
        var bytes = data.ToArray();
        try
        {
            // The print queue can block (a driver waiting for a device), so the call runs off-thread with a timeout.
            var outcome = await Task.Run(() => Send(bytes), CancellationToken.None).WaitAsync(timeout, cancellationToken);
            if (outcome is { } problem)
            {
                _logger.LogWarning("{Device} did not print: {Problem}", Description, problem);
                return Result.Failure(HardwareErrors.Unavailable(Description, problem));
            }

            return Result.Success();
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("{Device} timed out after {Timeout}.", Description, timeout);
            return Result.Failure(HardwareErrors.Timeout(Description));
        }
    }

    public async Task<DeviceStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return DeviceStatus.Unavailable("Windows printers can only be used on Windows.");
        try
        {
            return await Task.Run(() => WindowsPrinters.Status(printerName), CancellationToken.None).WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return DeviceStatus.Unavailable($"{Description} did not answer in time.");
        }
    }

    /// <summary>Null when the bytes were handed to the queue; otherwise why not.</summary>
    private string? Send(byte[] bytes)
    {
        var status = WindowsPrinters.Status(printerName);
        if (status.State != DeviceState.Ready) return status.Message;

        if (!WindowsPrinters.Native.OpenPrinter(printerName, out var handle, IntPtr.Zero))
            return $"The printer cannot be opened ({Win32Message()}).";
        try
        {
            var document = new WindowsPrinters.Native.DocInfo1 { DocumentName = "GenericPOS", OutputFile = null, DataType = "RAW" };
            if (WindowsPrinters.Native.StartDocPrinter(handle, 1, ref document) == 0)
                return $"The print job could not be started ({Win32Message()}).";
            try
            {
                if (!WindowsPrinters.Native.StartPagePrinter(handle)) return $"The print job could not be started ({Win32Message()}).";
                var ok = WindowsPrinters.Native.WritePrinter(handle, bytes, bytes.Length, out var written);
                WindowsPrinters.Native.EndPagePrinter(handle);
                if (!ok || written != bytes.Length) return $"The printer took {written} of {bytes.Length} bytes ({Win32Message()}).";
                return null;
            }
            finally
            {
                WindowsPrinters.Native.EndDocPrinter(handle);
            }
        }
        finally
        {
            WindowsPrinters.Native.ClosePrinter(handle);
        }
    }

    private static string Win32Message() => new Win32Exception(Marshal.GetLastWin32Error()).Message;
}

/// <summary>The printers installed in Windows and the state of their queues (MISS-07a).</summary>
public static class WindowsPrinters
{
    /// <summary>The names of the printers installed for this user (local and connected), sorted; empty outside Windows.</summary>
    public static IReadOnlyList<string> Installed()
    {
        if (!OperatingSystem.IsWindows()) return [];
        const int local = 0x2, connections = 0x4;
        Native.EnumPrinters(local | connections, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0) return [];
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!Native.EnumPrinters(local | connections, null, 4, buffer, needed, out _, out var returned)) return [];
            var size = Marshal.SizeOf<Native.PrinterInfo4>();
            return Enumerable.Range(0, returned)
                .Select(i => Marshal.PtrToStructure<Native.PrinterInfo4>(buffer + i * size))
                .Select(info => Marshal.PtrToStringUni(info.PrinterName) ?? string.Empty)
                .Where(name => name.Length > 0)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The queue's state in plain words: Ready, or why it cannot print now.</summary>
    public static DeviceStatus Status(string printerName)
    {
        if (!OperatingSystem.IsWindows()) return DeviceStatus.Unavailable("Windows printers can only be used on Windows.");
        if (!Native.OpenPrinter(printerName, out var handle, IntPtr.Zero))
            return DeviceStatus.Unavailable($"The Windows printer '{printerName}' was not found. Check the name under Settings > Printers & scanners.");
        try
        {
            Native.GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return DeviceStatus.Unavailable($"The state of the Windows printer '{printerName}' cannot be read.");
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!Native.GetPrinter(handle, 2, buffer, needed, out _))
                    return DeviceStatus.Unavailable($"The state of the Windows printer '{printerName}' cannot be read.");
                var info = Marshal.PtrToStructure<Native.PrinterInfo2>(buffer);
                return Describe(printerName, info.Status, info.Attributes, info.Jobs);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Native.ClosePrinter(handle);
        }
    }

    // PRINTER_STATUS_* and PRINTER_ATTRIBUTE_WORK_OFFLINE (winspool.h)
    private static readonly (uint Flag, string Problem)[] Blocking =
    [
        (0x00000400, "is set to 'Use Printer Offline' in Windows"),   // attribute, checked separately
        (0x00000080, "is offline (switched off or not connected)"),
        (0x00001000, "is not available"),
        (0x00000010, "is out of paper"),
        (0x00000040, "has a paper problem"),
        (0x00000008, "has a paper jam"),
        (0x00400000, "has its cover open"),
        (0x00100000, "needs attention"),
        (0x00000002, "reports an error"),
        (0x00000001, "is paused in Windows"),
        (0x00000004, "is being removed from Windows"),
    ];

    /// <summary>Maps the Windows queue status and attributes to a device status (pure: tested without a printer).</summary>
    public static DeviceStatus Describe(string printerName, uint status, uint attributes, uint waitingJobs)
    {
        const uint workOffline = 0x00000400;
        var problems = new List<string>();
        if ((attributes & workOffline) != 0) problems.Add(Blocking[0].Problem);
        problems.AddRange(Blocking.Skip(1).Where(b => (status & b.Flag) != 0).Select(b => b.Problem));
        if (problems.Count == 0) return DeviceStatus.Ready();

        var waiting = waitingJobs > 0 ? $" {waitingJobs} job(s) are waiting in its queue." : string.Empty;
        return DeviceStatus.Unavailable($"The printer '{printerName}' {string.Join(" and ", problems)}.{waiting}");
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DocInfo1
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
            [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PrinterInfo4
        {
            public IntPtr PrinterName;
            public IntPtr ServerName;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PrinterInfo2
        {
            public IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location, DevMode, SepFile, PrintProcessor, DataType, Parameters, SecurityDescriptor;
            public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePagesPerMinute;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        internal static extern bool ClosePrinter(IntPtr handle);

        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int StartDocPrinter(IntPtr handle, int level, ref DocInfo1 document);

        [DllImport("winspool.drv", SetLastError = true)]
        internal static extern bool EndDocPrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        internal static extern bool StartPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        internal static extern bool EndPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        internal static extern bool WritePrinter(IntPtr handle, byte[] bytes, int count, out int written);

        [DllImport("winspool.drv", EntryPoint = "GetPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool GetPrinter(IntPtr handle, int level, IntPtr buffer, int size, out int needed);

        [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool EnumPrinters(int flags, string? name, int level, IntPtr buffer, int size, out int needed, out int returned);
    }
}
