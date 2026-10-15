using System.Windows;
using Platform.Presentation.Localization;

namespace Client.Desktop.Shell;

/// <summary>
/// MISS-08 (found by the owner in Arabic): the reading direction goes on a window's CONTENT, never on the window itself. A window whose
/// FlowDirection is RightToLeft gets a mirrored frame from Windows (title bar, close buttons, position mirrored) and WPF then maps mouse
/// clicks wrongly, so the mouse did nothing after switching to Arabic. The content mirrors; the frame stays as Windows draws it.
/// </summary>
public static class WindowDirection
{
    /// <summary>Gives the window's content the direction of the display language; the window itself stays left-to-right.</summary>
    public static void Apply(Window window)
    {
        window.FlowDirection = FlowDirection.LeftToRight;
        if (window.Content is FrameworkElement content)
            content.FlowDirection = UiCulture.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    /// <summary>A window larger than the screen opens maximised instead of running off its edges (small and laptop screens).</summary>
    public static void FitToScreen(Window window)
    {
        var area = SystemParameters.WorkArea;
        if (window.Width > area.Width || window.Height > area.Height)
            window.WindowState = WindowState.Maximized;
    }

    /// <summary>A replacing shell window (another language, another user) opens exactly where the previous one was.</summary>
    public static void TakePlaceOf(Window window, Window previous)
    {
        var bounds = previous.WindowState == WindowState.Normal ? new Rect(previous.Left, previous.Top, previous.Width, previous.Height) : previous.RestoreBounds;
        if (!bounds.IsEmpty)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            (window.Left, window.Top, window.Width, window.Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }

        window.WindowState = previous.WindowState == WindowState.Minimized ? WindowState.Normal : previous.WindowState;
    }
}
