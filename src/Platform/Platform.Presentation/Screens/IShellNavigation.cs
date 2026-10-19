namespace Platform.Presentation.Screens;

/// <summary>
/// Lets a screen ask the shell to rebuild its navigation after something that changes what the user may open (for example activating
/// the license unlocks the licensed screens immediately), and to open another module's screen for a record without knowing that module
/// (MISS-10b, <see cref="ScreenRequest"/>). Implemented by the desktop shell.
/// </summary>
public interface IShellNavigation
{
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>True when a screen the signed-in user may open now handles <paramref name="requestKind"/> (a button that asks for it shows only then).</summary>
    bool CanOpenFor(string requestKind) => false;

    /// <summary>Opens the screen that handles the request and passes the request to it; false when no screen the user may open handles it.</summary>
    Task<bool> OpenForAsync(ScreenRequest request, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
