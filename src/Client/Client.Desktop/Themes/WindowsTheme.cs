using Microsoft.Win32;

namespace Client.Desktop.Themes;

/// <summary>
/// MISS-08d: the Windows light/dark setting for apps (Settings > Personalisation > Colours > "Choose your app mode"). Read when the start
/// screen and the shell open; a change while the application runs applies at the next sign-in (owner's decision).
/// </summary>
public static class WindowsTheme
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when Windows asks apps to be dark; false when it asks for light or the setting cannot be read.</summary>
    public static bool AppsUseDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;   // a preference: never fail a window over it
        }
    }
}
