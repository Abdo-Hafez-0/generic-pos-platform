namespace Platform.Presentation.Screens;

/// <summary>
/// MISS-11c-3: a module's own window (a dialog it opens over the shell) is decorated like the shell's windows - the desktop sets
/// <see cref="Decorate"/> at start (the dark title bar in the dark theme); a module calls <see cref="Follow"/> with its window. The value is
/// a WPF Window; this library does not know WPF, so it is passed as an object.
/// </summary>
public static class OwnedWindows
{
    public static Action<object>? Decorate { get; set; }

    public static void Follow(object window) => Decorate?.Invoke(window);
}
