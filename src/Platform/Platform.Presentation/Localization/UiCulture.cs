using System.Globalization;
using System.Reflection;

namespace Platform.Presentation.Localization;

/// <summary>
/// The display language of the desktop (FIX-01 decision 4). Every UI text comes from resources of the current display culture; layouts
/// follow <see cref="IsRightToLeft"/> so that a right-to-left language (Arabic, FIX-13) needs translations, not new screens.
///
/// FIX-13b: the display language changes while the application runs (each user has their own). .NET restores a thread's culture when an
/// async method that changed it completes, so the display language must not live in the thread culture alone: <see cref="Apply"/> keeps it
/// in <see cref="Current"/> and sets it on every generated resource class (their static <c>Culture</c>), so a screen opened later still
/// reads the right texts. Number and date FORMATTING stays the installation's (<see cref="ApplyFormatting"/>): amounts are typed and shown
/// the same way whatever the screen language.
/// </summary>
public static class UiCulture
{
    /// <summary>The culture used when nothing (or something unknown) is configured.</summary>
    public const string Default = "en";

    private static CultureInfo _current = CultureInfo.GetCultureInfo(Default);

    /// <summary>The display language now in use.</summary>
    public static CultureInfo Current => _current;

    /// <summary>True when the display language writes right to left.</summary>
    public static bool IsRightToLeft => _current.TextInfo.IsRightToLeft;

    /// <summary>
    /// Makes <paramref name="cultureName"/> the display language: screen texts of every resource class, and the UI culture of the current
    /// thread and of threads started afterwards. An empty or unknown name falls back to <see cref="Default"/>: a typo must never stop the application.
    /// </summary>
    public static CultureInfo Apply(string? cultureName)
    {
        var culture = Resolve(cultureName);
        _current = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        foreach (var resources in FindResourceClasses(AppDomain.CurrentDomain.GetAssemblies()))
            resources.SetValue(null, culture);
        WatchLoadedAssemblies();
        return culture;
    }

    /// <summary>The culture for numbers and dates (the installation's), for the current thread and threads started afterwards.</summary>
    public static CultureInfo ApplyFormatting(string? cultureName)
    {
        var culture = Resolve(cultureName);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
        return culture;
    }

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

    /// <summary>The <c>Culture</c> property of every generated (strongly typed .resx) resource class of the application's own assemblies.</summary>
    internal static IReadOnlyList<PropertyInfo> FindResourceClasses(IEnumerable<Assembly> assemblies)
        => assemblies
            .Where(a => !a.IsDynamic && a.GetName().Name is { } name && !name.StartsWith("System", StringComparison.Ordinal)
                        && !name.StartsWith("Microsoft", StringComparison.Ordinal) && !name.StartsWith("netstandard", StringComparison.Ordinal))
            .SelectMany(Types)
            .Where(t => t.IsClass)
            .Select(t => (Type: t, Culture: t.GetProperty("Culture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)))
            .Where(x => x.Culture is { PropertyType: var type, CanWrite: true } && type == typeof(CultureInfo)
                        && x.Type.GetProperty("ResourceManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.PropertyType == typeof(System.Resources.ResourceManager))
            .Select(x => x.Culture!)
            .ToList();

    private static int _watching;

    /// <summary>A module assembly loaded after the language was chosen gets it too.</summary>
    private static void WatchLoadedAssemblies()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
        {
            foreach (var resources in FindResourceClasses([e.LoadedAssembly]))
                resources.SetValue(null, _current);
        };
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }
}
