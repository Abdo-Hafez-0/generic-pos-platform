using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Client.Desktop.Themes;

/// <summary>
/// MISS-08d (found on the real executable): Windows draws a window's title bar itself, white over the dark theme. Windows 10 (1809+) and 11
/// draw it dark when asked through the window's "immersive dark mode" attribute; older Windows ignore it and keep the light bar.
/// </summary>
public static class TitleBar
{
    private const int UseImmersiveDarkMode = 20;          // Windows 10 20H1 and later, Windows 11
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Makes the title bar follow the theme now (once the window has its handle) and whenever it is shown again.</summary>
    public static void Follow(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window, ThemeManager.Current == ThemeKind.Dark);
        Apply(window, ThemeManager.Current == ThemeKind.Dark);
    }

    /// <summary>Dark or light title bar for the window; nothing before the window has a handle.</summary>
    public static void Apply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
    }
}
