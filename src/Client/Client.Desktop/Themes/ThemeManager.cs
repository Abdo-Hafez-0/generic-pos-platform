using System.Windows;

namespace Client.Desktop.Themes;

/// <summary>The two colour sets of the Matjar theme (MISS-08).</summary>
public enum ThemeKind
{
    Light = 0,
    Dark = 1
}

/// <summary>
/// MISS-08: the application's theme. App.xaml merges two dictionaries: the colour set (Light.xaml or Dark.xaml) first, the control
/// styles (Controls.xaml) second. Screens refer to the colours by name with DynamicResource, so replacing the first dictionary restyles
/// every open window at once.
/// </summary>
public static class ThemeManager
{
    private static Uri ColoursOf(ThemeKind kind) => new($"pack://application:,,,/Client.Desktop;component/Themes/{kind}.xaml", UriKind.Absolute);

    private static readonly Uri Controls = new("pack://application:,,,/Client.Desktop;component/Themes/Controls.xaml", UriKind.Absolute);

    /// <summary>The theme now applied to the application, or Light when none is.</summary>
    public static ThemeKind Current { get; private set; } = ThemeKind.Light;

    /// <summary>Puts the colour set in place (the control styles stay). Does nothing without a running application.</summary>
    public static void Apply(ThemeKind kind)
    {
        if (Application.Current?.Resources is not { } resources) return;
        Apply(resources, kind);
    }

    /// <summary>Puts the theme into a resource dictionary: [colours, controls], replacing the colour set already there.</summary>
    public static void Apply(ResourceDictionary resources, ThemeKind kind)
    {
        var merged = resources.MergedDictionaries;
        var colours = new ResourceDictionary { Source = ColoursOf(kind) };
        if (merged.Count == 0) merged.Add(colours);
        else merged[0] = colours;
        if (merged.Count == 1) merged.Add(new ResourceDictionary { Source = Controls });
        Current = kind;
    }
}
