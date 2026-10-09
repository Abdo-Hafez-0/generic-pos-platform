using Client.Hardware.Configuration;
using Client.Hardware.Scanner;
using Client.Host.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Hardware;

namespace Client.Hardware;

/// <summary>Marker used by architecture tests to locate this assembly.</summary>
public sealed class ClientHardwareAssemblyMarker;

/// <summary>A sink that ignores input, used when no keyboard-wedge scanner is configured so the UI never needs a null check.</summary>
internal sealed class NullKeyboardInputSink : IKeyboardInputSink
{
    public bool OnCharacter(char character) => false;
}

public static class HardwareServiceCollectionExtensions
{
    /// <summary>
    /// Registers the peripheral abstractions with the adapters selected by the "Hardware" configuration section. Every device is a
    /// singleton chosen at start-up; devices are never constructed by business code. With no configuration every device is a
    /// "not configured" placeholder, and the POS works without any peripheral. No network or internet access is needed to start.
    /// </summary>
    public static IServiceCollection AddClientHardware(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new HardwareOptions();
        configuration.GetSection(HardwareOptions.SectionName).Bind(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);

        services.AddSingleton<IBarcodeScanner>(sp => HardwareFactory.CreateScanner(options.Scanner, sp.GetRequiredService<TimeProvider>(), Logger(sp)));
        // FIX-13a: the desktop registers an IReceiptImageRenderer (fonts); without one, receipts are ASCII text as before
        services.AddSingleton<IReceiptPrinter>(sp => HardwareFactory.CreateReceiptPrinter(options.ReceiptPrinter, Logger(sp), sp.GetService<IReceiptImageRenderer>()));
        services.AddSingleton<ILabelPrinter>(sp => HardwareFactory.CreateLabelPrinter(options.LabelPrinter, Logger(sp)));
        services.AddSingleton<ICashDrawer>(sp => HardwareFactory.CreateCashDrawer(options.CashDrawer, options.ReceiptPrinter, Logger(sp)));
        services.AddSingleton<IScale>(_ => HardwareFactory.CreateScale(options.Scale));

        // The UI forwards raw key presses here; it never sees which scanner (if any) is behind it.
        services.AddSingleton<IKeyboardInputSink>(sp => sp.GetRequiredService<IBarcodeScanner>() as IKeyboardInputSink ?? new NullKeyboardInputSink());
        return services;
    }

    private static ILogger? Logger(IServiceProvider sp) => sp.GetService<ILoggerFactory>()?.CreateLogger("Client.Hardware");
}

/// <summary>Composition-root registration of the hardware adapters (same pattern as the licensing and updater hosting modules).</summary>
public sealed class HardwareHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.AddClientHardware(context.Configuration);
}
