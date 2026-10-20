namespace Platform.Presentation.Screens;

/// <summary>
/// MISS-11a-3: a module's warning for the shell's status bar (for example "3 electronic receipts were refused by the tax authority"). The shell
/// asks only the providers whose <see cref="RequiredCapability"/> the signed-in user holds - after sign-in, when a screen is opened, and every
/// few minutes - and shows what they say. Registered by the module's UI as a singleton; reaches its data per call (through the action runner).
/// </summary>
public interface IShellNoticeProvider
{
    /// <summary>Who the warning is for: the capability that lets someone act on it.</summary>
    string RequiredCapability { get; }

    /// <summary>The warning in plain words, or null when all is well. Never throws for a business reason.</summary>
    Task<string?> GetNoticeAsync(CancellationToken cancellationToken = default);
}
