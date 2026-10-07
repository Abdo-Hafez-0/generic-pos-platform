namespace Platform.Presentation.Screens;

/// <summary>
/// Lets a screen ask the shell to rebuild its navigation after something that changes what the user may open (for example activating
/// the license unlocks the licensed screens immediately). Implemented by the desktop shell.
/// </summary>
public interface IShellNavigation
{
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
