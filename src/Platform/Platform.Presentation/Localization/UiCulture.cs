using System.Globalization;

namespace Platform.Presentation.Localization;

/// <summary>
/// The display language of the desktop (FIX-01 decision 4). Every UI text comes from resources of the current UI culture; layouts
/// follow <see cref="IsRightToLeft"/> so that a right-to-left language (Arabic, FIX-13) needs translations, not new screens.
/// </summary>
public static class UiCulture
{
    /// <summary>The culture used when nothing (or something unknown) is configured.</summary>
    public const string Default = "en";

    /// <summary>
    /// Makes <paramref name="cultureName"/> the UI and formatting culture of the process (every thread started afterwards included).
    /// An empty or unknown name falls back to <see cref="Default"/>: a typo in configuration must never stop the application.
    /// </summary>
    public static CultureInfo Apply(string? cultureName)
    {
        var culture = Resolve(cultureName);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return culture;
    }

    /// <summary>True when the current UI culture writes right to left.</summary>
    public static bool IsRightToLeft => CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft;

    public static CultureInfo Resolve(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
            return CultureInfo.GetCultureInfo(Default);

        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName.Trim(), predefinedOnly: true);
            return culture.Equals(CultureInfo.InvariantCulture) ? CultureInfo.GetCultureInfo(Default) : culture;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo(Default);
        }
    }
}
