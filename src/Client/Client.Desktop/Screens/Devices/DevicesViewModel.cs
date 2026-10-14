using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Client.Desktop.Resources;
using Client.Desktop.Shell;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using POS.Application.Devices;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;

namespace Client.Desktop.Screens.Devices;

/// <summary>One choice in a list: the value and its text in the display language.</summary>
public sealed record DeviceChoice<T>(T Value, string Text)
{
    /// <summary>What a screen reader announces for the chosen item (found in the real run: the record's default text was read out).</summary>
    public override string ToString() => Text;
}

/// <summary>
/// Administration > Devices (MISS-07b): how this computer reaches its receipt printer (installed in Windows, or on the network), the cash
/// drawer (through the receipt printer), the barcode scanner (keyboard mode) and the label printer. Check, test receipt, open the drawer and
/// test label use the settings AS ENTERED, so a choice is proven before it is kept; Save keeps them and they apply after a restart ("Restart
/// now"). The scanner test shows what the scanner in use now reads. Everything goes through the POS module's handlers (devices.manage).
/// </summary>
public sealed class DevicesViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private readonly IApplicationRestarter _restarter;
    private readonly IPOSBarcodeInput? _scanner;
    private SynchronizationContext? _ui;
    private bool _listening;

    private DeviceChoice<POSPrinterLink>? _receiptKind, _labelKind;
    private DeviceChoice<int>? _drawerPin;
    private string? _receiptPrinterName, _labelPrinterName;
    private string _receiptHost = string.Empty, _receiptPort = "9100", _labelHost = string.Empty, _labelPort = "9100";
    private int _charactersPerLine = 42;
    private bool _cutPaper = true, _drawerUsed, _scannerUsed, _restartNeeded;
    private string _receiptState = string.Empty, _drawerState = string.Empty, _scannerState = string.Empty, _labelState = string.Empty;
    private string _lastScan = DevicesText.ScanNone;

    public DevicesViewModel(IUiActionRunner runner, IApplicationRestarter restarter, IPOSBarcodeInput? scanner = null)
    {
        _runner = runner;
        _restarter = restarter;
        _scanner = scanner;
        PrinterKinds = Kinds();
        LabelKinds = Kinds();
        DrawerPins = [new(0, DevicesText.PinTwo), new(1, DevicesText.PinFive)];
        CheckCommand = Command(() => ShowCheckAsync(CancellationToken.None));
        TestReceiptCommand = Command(() => TestAsync(POSDeviceTest.Receipt, DevicesText.TestReceiptDone), () => ReceiptKind?.Value != POSPrinterLink.None);
        OpenDrawerCommand = Command(() => TestAsync(POSDeviceTest.Drawer, DevicesText.DrawerOpened), () => DrawerUsed);
        TestLabelCommand = Command(() => TestAsync(POSDeviceTest.Label, DevicesText.TestLabelDone), () => LabelKind?.Value != POSPrinterLink.None);
        SaveCommand = Command(SaveAsync);
        RestartNowCommand = Command(() => { _restarter.Restart(); return Task.CompletedTask; }, () => RestartNeeded);
    }

    private static List<DeviceChoice<POSPrinterLink>> Kinds()
        => [new(POSPrinterLink.None, DevicesText.KindNone), new(POSPrinterLink.WindowsPrinter, DevicesText.KindWindows), new(POSPrinterLink.Network, DevicesText.KindNetwork)];

    public ICommand CheckCommand { get; }
    public ICommand TestReceiptCommand { get; }
    public ICommand OpenDrawerCommand { get; }
    public ICommand TestLabelCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RestartNowCommand { get; }

    // ------------------------------------------------------------------ the choices

    public List<DeviceChoice<POSPrinterLink>> PrinterKinds { get; }
    public List<DeviceChoice<POSPrinterLink>> LabelKinds { get; }
    public IReadOnlyList<DeviceChoice<int>> DrawerPins { get; }
    public IReadOnlyList<DeviceChoice<int>> LineWidths { get; } = [new(32, DevicesText.Width32), new(42, DevicesText.Width42), new(48, DevicesText.Width48)];
    public ObservableCollection<string> WindowsPrinters { get; } = [];
    public bool HasNoWindowsPrinters => WindowsPrinters.Count == 0;

    // ------------------------------------------------------------------ the settings as entered

    public DeviceChoice<POSPrinterLink>? ReceiptKind
    {
        get => _receiptKind;
        set
        {
            if (Set(ref _receiptKind, value)) { Raise(nameof(IsReceiptWindows)); Raise(nameof(IsReceiptNetwork)); }
        }
    }

    public bool IsReceiptWindows => ReceiptKind?.Value == POSPrinterLink.WindowsPrinter;
    public bool IsReceiptNetwork => ReceiptKind?.Value == POSPrinterLink.Network;
    public string? ReceiptPrinterName { get => _receiptPrinterName; set => Set(ref _receiptPrinterName, value); }
    public string ReceiptHost { get => _receiptHost; set => Set(ref _receiptHost, value); }
    public string ReceiptPort { get => _receiptPort; set => Set(ref _receiptPort, value); }
    public int CharactersPerLine { get => _charactersPerLine; set => Set(ref _charactersPerLine, value); }
    public bool CutPaper { get => _cutPaper; set => Set(ref _cutPaper, value); }

    /// <summary>The drawer is connected to the receipt printer (the usual way).</summary>
    public bool DrawerUsed { get => _drawerUsed; set => Set(ref _drawerUsed, value); }
    public DeviceChoice<int>? DrawerPin { get => _drawerPin; set => Set(ref _drawerPin, value); }

    /// <summary>A USB scanner in keyboard mode.</summary>
    public bool ScannerUsed { get => _scannerUsed; set => Set(ref _scannerUsed, value); }

    public DeviceChoice<POSPrinterLink>? LabelKind
    {
        get => _labelKind;
        set
        {
            if (Set(ref _labelKind, value)) { Raise(nameof(IsLabelWindows)); Raise(nameof(IsLabelNetwork)); }
        }
    }

    public bool IsLabelWindows => LabelKind?.Value == POSPrinterLink.WindowsPrinter;
    public bool IsLabelNetwork => LabelKind?.Value == POSPrinterLink.Network;
    public string? LabelPrinterName { get => _labelPrinterName; set => Set(ref _labelPrinterName, value); }
    public string LabelHost { get => _labelHost; set => Set(ref _labelHost, value); }
    public string LabelPort { get => _labelPort; set => Set(ref _labelPort, value); }

    // ------------------------------------------------------------------ states and the scanner test

    public string ReceiptState { get => _receiptState; private set => Set(ref _receiptState, value); }
    public string DrawerState { get => _drawerState; private set => Set(ref _drawerState, value); }
    public string ScannerState { get => _scannerState; private set => Set(ref _scannerState, value); }
    public string LabelState { get => _labelState; private set => Set(ref _labelState, value); }

    /// <summary>What the scanner in use now last read (the scanner test).</summary>
    public string LastScan { get => _lastScan; private set => Set(ref _lastScan, value); }

    /// <summary>The saved settings differ from the ones in use: "Restart now" applies them.</summary>
    public bool RestartNeeded { get => _restartNeeded; private set => Set(ref _restartNeeded, value); }

    // ------------------------------------------------------------------ loading and leaving

    public async Task OnNavigatedToAsync(CancellationToken cancellationToken = default)
    {
        await BusyAsync(() => LoadAsync(cancellationToken));
        await ListenAsync(cancellationToken);
    }

    public async Task OnNavigatedFromAsync(CancellationToken cancellationToken = default)
    {
        if (_scanner is null || !_listening) return;
        _listening = false;
        _scanner.ScanProcessed -= OnScanned;
        await _scanner.StopAsync(cancellationToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var overview = await _runner.RunAsync((scope, ct) => scope.Get<GetDeviceSetupQueryHandler>().HandleAsync(new GetDeviceSetupQuery(), ct), cancellationToken);
        if (!Accept(overview)) return;
        var saved = overview.Value.Saved;
        RestartNeeded = overview.Value.RestartNeeded;

        WindowsPrinters.Clear();
        foreach (var name in overview.Value.WindowsPrinters) WindowsPrinters.Add(name);
        foreach (var name in new[] { saved.ReceiptPrinter.PrinterName, saved.LabelPrinter.PrinterName })
            if (!string.IsNullOrWhiteSpace(name) && !WindowsPrinters.Contains(name)) WindowsPrinters.Add(name);   // saved, but not installed now
        Raise(nameof(HasNoWindowsPrinters));

        ReceiptKind = Choose(PrinterKinds, saved.ReceiptPrinter.Link);
        ReceiptPrinterName = saved.ReceiptPrinter.PrinterName;
        ReceiptHost = saved.ReceiptPrinter.Host ?? string.Empty;
        ReceiptPort = saved.ReceiptPrinter.Port.ToString(CultureInfo.InvariantCulture);
        CharactersPerLine = saved.CharactersPerLine;
        CutPaper = saved.CutPaper;
        DrawerUsed = saved.DrawerThroughReceiptPrinter;
        DrawerPin = DrawerPins.FirstOrDefault(p => p.Value == saved.DrawerPin) ?? DrawerPins[0];
        ScannerUsed = saved.KeyboardScanner;
        LabelKind = Choose(LabelKinds, saved.LabelPrinter.Link);
        LabelPrinterName = saved.LabelPrinter.PrinterName;
        LabelHost = saved.LabelPrinter.Host ?? string.Empty;
        LabelPort = saved.LabelPrinter.Port.ToString(CultureInfo.InvariantCulture);

        await ShowCheckAsync(cancellationToken);
    }

    /// <summary>The choice for a link; a link set in the configuration that the screen does not offer is shown as such and kept.</summary>
    private static DeviceChoice<POSPrinterLink> Choose(List<DeviceChoice<POSPrinterLink>> choices, POSPrinterLink link)
    {
        if (choices.FirstOrDefault(c => c.Value == link) is { } found) return found;
        var configured = new DeviceChoice<POSPrinterLink>(POSPrinterLink.Configured, DevicesText.KindConfigured);
        choices.Add(configured);
        return configured;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        if (_scanner is null || _listening) return;
        _ui = SynchronizationContext.Current;
        _scanner.ScanProcessed += OnScanned;
        _listening = true;
        await _scanner.StartAsync(cancellationToken);
    }

    /// <summary>No cart is open on this screen, so every scan is reported here with its code: exactly what the scanner sent.</summary>
    private void OnScanned(object? sender, POSScanOutcome scan)
    {
        var text = string.Format(CultureInfo.CurrentCulture, DevicesText.ScanRead, Ltr(scan.Barcode), scan.Barcode.Length, Ltr(DateTime.Now.ToString("T", CultureInfo.CurrentCulture)));
        if (_ui is null) LastScan = text;
        else _ui.Post(_ => LastScan = text, null);
    }

    // ------------------------------------------------------------------ actions

    /// <summary>The settings as entered.</summary>
    public POSDeviceSettings Entered()
        => new(
            Printer(ReceiptKind, ReceiptPrinterName, ReceiptHost, ReceiptPort),
            CharactersPerLine, CutPaper, DrawerUsed, DrawerPin?.Value ?? 0, ScannerUsed,
            Printer(LabelKind, LabelPrinterName, LabelHost, LabelPort));

    private static POSPrinterSetup Printer(DeviceChoice<POSPrinterLink>? kind, string? name, string host, string port)
        => new(kind?.Value ?? POSPrinterLink.None, name, host.Trim(), int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0);

    private async Task ShowCheckAsync(CancellationToken cancellationToken)
    {
        var settings = Entered();
        var check = await _runner.RunAsync((scope, ct) => scope.Get<CheckDevicesCommandHandler>().HandleAsync(new CheckDevicesCommand(settings), ct), cancellationToken);
        if (!Accept(check)) return;
        ReceiptState = Describe(check.Value.ReceiptPrinter);
        DrawerState = Describe(check.Value.CashDrawer);
        ScannerState = Describe(check.Value.Scanner);
        LabelState = Describe(check.Value.LabelPrinter);
    }

    private static string Describe(POSDeviceState state)
        => !state.IsUsed ? DevicesText.StateNotUsed
            : state.IsReady ? DevicesText.StateReady
            : string.Format(CultureInfo.CurrentCulture, DevicesText.StateNotReady, Ltr(state.Problem ?? string.Empty));

    private async Task TestAsync(POSDeviceTest test, string done)
    {
        var settings = Entered();
        var tested = await _runner.RunAsync((scope, ct) => scope.Get<TestDeviceCommandHandler>().HandleAsync(new TestDeviceCommand(settings, test), ct));
        if (!Accept(tested)) return;
        StatusMessage = done;
    }

    private async Task SaveAsync()
    {
        var settings = Entered();
        var saved = await _runner.RunAsync((scope, ct) => scope.Get<SaveDeviceSettingsCommandHandler>().HandleAsync(new SaveDeviceSettingsCommand(settings), ct));
        if (!Accept(saved)) return;
        RestartNeeded = saved.Value;
        StatusMessage = saved.Value ? DevicesText.Saved : DevicesText.SavedNoChange;
    }

    /// <summary>Names, addresses and the English device messages keep their order inside Arabic text (FIX-13c).</summary>
    private static string Ltr(string value) => "‎" + value + "‎";
}
