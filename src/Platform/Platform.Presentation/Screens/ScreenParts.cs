namespace Platform.Presentation.Screens;

/// <summary>
/// MISS-08c: parts of a screen the shell shows outside the screen itself, found by resource key so that a module UI needs nothing from
/// the shell. A view puts the part in its own resources under the key (XAML: <c>x:Key="{x:Static screens:ScreenParts.HeaderActions}"</c>);
/// the shell shows it bound to the view's own view model.
/// </summary>
public static class ScreenParts
{
    /// <summary>The screen's main action(s) - "New product", "Back up now" - shown at the end of the shell's title row.</summary>
    public const string HeaderActions = "Screen.HeaderActions";
}
