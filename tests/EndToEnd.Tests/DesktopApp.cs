using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace EndToEnd.Tests;

/// <summary>
/// The REAL Client.Desktop.exe driven through Windows UI Automation. Everything goes to this application's own windows: text through the
/// UI Automation value pattern, password boxes and scanner keystrokes as window messages posted to its window (never global keyboard input),
/// screenshots of its window only (PrintWindow, never the screen).
/// </summary>
internal sealed class DesktopApp : IDisposable
{
    private static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(30);
    private readonly Process _process;

    public DesktopApp(string root, IReadOnlyDictionary<string, string> settings)
    {
        Root = root;
        var exe = Path.Combine(Repo.Root, "src", "Client", "Client.Desktop", "bin", Repo.Configuration, "net10.0-windows", "Client.Desktop.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException($"Build Client.Desktop first: {exe} does not exist.");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var (key, value) in settings) start.Environment["GENERICPOS_" + key.Replace(":", "__")] = value;
        _process = Process.Start(start)!;
    }

    public string Root { get; }
    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;

    // ------------------------------------------------------------------ windows

    public AutomationElement Window(Func<AutomationElement, bool> which, string what, TimeSpan? wait = null)
        => Until(() =>
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id));
            return windows.Cast<AutomationElement>().FirstOrDefault(which);
        }, what, wait ?? TimeSpan.FromSeconds(90));

    /// <summary>The start screen (first-run setup or sign-in).</summary>
    public AutomationElement SignInWindow() => Window(w => Find(w, "PrimaryButton") is not null, "the start screen");

    /// <summary>The shell (its navigation is there).</summary>
    public AutomationElement MainWindow() => Window(w => Find(w, "Navigation") is not null, "the main window");

    // ------------------------------------------------------------------ elements

    public static AutomationElement? Find(AutomationElement root, string automationId)
        => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    public static AutomationElement Get(AutomationElement root, string automationId, TimeSpan? wait = null)
        => Until(() => Find(root, automationId), automationId, wait ?? DefaultWait);

    public static string Text(AutomationElement root, string automationId)
    {
        var element = Find(root, automationId);
        if (element is null) return string.Empty;
        return element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? ((ValuePattern)value).Current.Value : element.Current.Name;
    }

    public static void SetText(AutomationElement root, string automationId, string text)
        => ((ValuePattern)Get(root, automationId).GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

    /// <summary>Invokes a button once it is enabled (a command that cannot run yet keeps it disabled).</summary>
    public static void Click(AutomationElement root, string automationId)
    {
        var button = Until(() => Find(root, automationId) is { Current.IsEnabled: true } e ? e : null, automationId + " enabled", DefaultWait);
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    /// <summary>Opens a screen from the shell's navigation by its title.</summary>
    public static void Open(AutomationElement main, string title)
    {
        var entry = Until(() => main.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, title),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))), "navigation " + title, DefaultWait);
        ((InvokePattern)entry.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        Thread.Sleep(700);
    }

    /// <summary>Chooses the item of a combo box whose text contains <paramref name="text"/>.</summary>
    public static void Choose(AutomationElement root, string automationId, string text)
    {
        var box = Get(root, automationId);
        var expand = (ExpandCollapsePattern)box.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        var item = Until(() => box.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().FirstOrDefault(i => i.Current.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                                                           || i.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Any(t => t.Current.Name.Contains(text, StringComparison.OrdinalIgnoreCase))),
            $"{automationId} item {text}", DefaultWait);
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        expand.Collapse();
    }

    /// <summary>The rows of a data grid, each as the texts of its cells.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Rows(AutomationElement root, string automationId)
    {
        var grid = Get(root, automationId);
        return grid.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)).Cast<AutomationElement>()
            .Select(row => (IReadOnlyList<string>)row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(t => t.Current.Name.Trim('‎')).ToList())
            .ToList();
    }

    /// <summary>Waits for a text to satisfy a condition; returns it.</summary>
    public static string WaitText(AutomationElement root, string automationId, Func<string, bool> ok, string what, TimeSpan? wait = null)
        => Until(() => Text(root, automationId) is var t && ok(t) ? t : null, what, wait ?? DefaultWait);

    // ------------------------------------------------------------------ keyboard (window messages to this application only)

    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmChar = 0x0102;

    /// <summary>Types into a password box (it offers no value pattern): focus it, then post the characters to the window.</summary>
    public static void TypeSecret(AutomationElement window, string automationId, string text)
    {
        Get(window, automationId).SetFocus();
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        foreach (var c in text)
        {
            PostMessage(hwnd, WmChar, c, 1);
            Thread.Sleep(15);
        }

        Thread.Sleep(200);
    }

    /// <summary>
    /// What a keyboard-wedge scanner does: the code typed very fast, then Enter, into the window - with the focus on an element that is
    /// not a text box (<paramref name="focusId"/>), because typing into a box is typing, not scanning.
    /// </summary>
    public static void Scan(AutomationElement window, string focusId, string code)
    {
        Get(window, focusId).SetFocus();
        Thread.Sleep(150);
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        foreach (var c in code)
        {
            var vk = char.IsDigit(c) || char.IsUpper(c) ? c : char.ToUpperInvariant(c);
            PostMessage(hwnd, WmKeyDown, vk, 1);
            PostMessage(hwnd, WmChar, c, 1);
            PostMessage(hwnd, WmKeyUp, vk, unchecked((int)0xC0000001));
        }

        PostMessage(hwnd, WmKeyDown, 0x0D, 1);
        PostMessage(hwnd, WmChar, '\r', 1);
        PostMessage(hwnd, WmKeyUp, 0x0D, unchecked((int)0xC0000001));
        Thread.Sleep(500);
    }

    // ------------------------------------------------------------------ screenshots (this window only)

    public static void Screenshot(AutomationElement window, string path)
    {
        try
        {
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            SetProcessDPIAware();
            GetWindowRect(hwnd, out var rect);
            using var bitmap = new Bitmap(Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                PrintWindow(hwnd, hdc, 2);
                graphics.ReleaseHdc(hdc);
            }

            bitmap.Save(path, ImageFormat.Png);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ExternalException or InvalidOperationException)
        {
            // best effort: a screenshot never hides the failure it documents
        }
    }

    // ------------------------------------------------------------------ waiting

    public static T Until<T>(Func<T?> probe, string what, TimeSpan wait) where T : class
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                if (probe() is { } found) return found;
            }
            catch (ElementNotAvailableException)
            {
            }

            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            Thread.Sleep(200);
        }
    }

    public void Close(AutomationElement window, TimeSpan? wait = null)
    {
        ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
        if (!_process.WaitForExit((int)(wait ?? TimeSpan.FromSeconds(60)).TotalMilliseconds)) throw new TimeoutException("The application did not close.");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
}
