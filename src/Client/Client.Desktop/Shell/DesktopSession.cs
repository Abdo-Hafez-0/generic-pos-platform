using System.Windows;
using Client.Desktop.Shell;
using Client.Desktop.Themes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Platform.Presentation.Localization;
using Users.Application.Security;

namespace Client.Desktop;

/// <summary>
/// Who works at the desktop, in which language (FIX-13b) and which colours (MISS-08d). The start screen always speaks the INSTALLATION's language (Ui:Culture);
/// after a sign-in the shell opens in the signed-in user's own language (Users: a per-user setting; none = the installation's). Because
/// screen texts are read when a window is built, a language change means a NEW shell window: <see cref="OpenShellAsync"/> builds it and
/// closes the old one. Sign-out returns to the start screen; closing the start screen ends the application.
/// </summary>
public sealed class DesktopSession(IServiceProvider services, IConfiguration configuration, ILogger<DesktopSession> logger)
{
    /// <summary>The installation's language (Ui:Culture), used by the start screen and by users without their own choice.</summary>
    public string? InstallationLanguage => configuration["Ui:Culture"];

    /// <summary>Shows the start screen (sign-in, first-run setup, password change) in the installation's language; true when someone signed in.</summary>
    public bool ShowSignIn(Action<SignInWindow>? prepare = null)
    {
        UiCulture.Apply(InstallationLanguage);
        MyTheme = null;
        ThemeManager.ApplyChoice(null);   // MISS-08d (owner): before anyone signs in, the colours follow Windows
        var signIn = services.GetRequiredService<SignInWindow>();
        prepare?.Invoke(signIn);
        return signIn.ShowDialog() == true;
    }

    /// <summary>The signed-in user's theme choice: "light", "dark", or null = like Windows (MISS-08d).</summary>
    public string? MyTheme { get; private set; }

    /// <summary>Opens a new shell window in the signed-in user's language and closes the previous one (if any).</summary>
    public async Task OpenShellAsync()
    {
        // MISS-08d: the user's own colours ("light", "dark"; none = like Windows, read now - a Windows change applies at the next sign-in)
        MyTheme = await ReadMyThemeAsync();
        var kind = ThemeManager.ApplyChoice(MyTheme);
        logger.LogInformation("Theme for this user: {Theme} ({Choice}).", kind, MyTheme ?? "like Windows");

        var language = await ReadMyLanguageAsync();
        var culture = UiCulture.Apply(string.IsNullOrWhiteSpace(language) ? InstallationLanguage : language);
        logger.LogInformation("Display language for this user: {Culture}.", culture.Name);

        var app = Application.Current;
        var previous = app.MainWindow as MainWindow;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;   // no shutdown while one shell window replaces the other
        await services.GetRequiredService<ShellViewModel>().ResetAsync();

        var window = services.GetRequiredService<MainWindow>();
        if (previous is not null) WindowDirection.TakePlaceOf(window, previous);   // MISS-08: the new language opens where the window was
        app.MainWindow = window;
        window.Show();
        previous?.CloseForReplacement();
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    /// <summary>Signs out, shows the start screen again and opens the shell for whoever signs in next; exits when nobody does.</summary>
    public async Task SignOutAsync(Window current)
    {
        using (var scope = services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InteractiveSignInService>().SignOutAsync();
        await services.GetRequiredService<ShellViewModel>().ResetAsync();
        logger.LogInformation("User signed out; returning to the start screen.");

        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        current.Hide();
        if (!ShowSignIn())
        {
            Application.Current.Shutdown();
            return;
        }

        await OpenShellAsync();
    }

    /// <summary>Saves the signed-in user's own language (null = the installation's) and reopens the shell in it.</summary>
    public async Task<Result> ChangeMyLanguageAsync(string? language)
    {
        Result saved;
        using (var scope = services.CreateScope())
        {
            var me = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            saved = await scope.ServiceProvider.GetRequiredService<Users.Application.Commands.SetUserLanguageCommandHandler>()
                .HandleAsync(new Users.Application.Commands.SetUserLanguageCommand(me.UserId, language));
        }

        if (saved.IsSuccess) await OpenShellAsync();
        return saved;
    }

    /// <summary>
    /// MISS-08d: saves the signed-in user's theme ("light", "dark", or null = like Windows) and applies it at once - the screens name their
    /// colours (DynamicResource), so the open windows restyle without reopening.
    /// </summary>
    public async Task<Result> ChangeMyThemeAsync(string? theme)
    {
        Result saved;
        using (var scope = services.CreateScope())
        {
            var me = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            saved = await scope.ServiceProvider.GetRequiredService<Users.Application.Commands.SetUserThemeCommandHandler>()
                .HandleAsync(new Users.Application.Commands.SetUserThemeCommand(me.UserId, theme));
        }

        if (saved.IsFailure) return saved;
        MyTheme = theme;
        ThemeManager.ApplyChoice(theme);
        return saved;
    }

    private async Task<string?> ReadMyThemeAsync()
    {
        try
        {
            using var scope = services.CreateScope();
            var me = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            if (!me.IsAuthenticated) return null;
            var theme = await scope.ServiceProvider.GetRequiredService<Users.Application.Queries.GetUserThemeQueryHandler>()
                .HandleAsync(new Users.Application.Queries.GetUserThemeQuery(me.UserId));
            return theme.IsSuccess ? theme.Value : null;
        }
        catch (Exception ex)
        {
            // the theme is a preference: never keep someone out of the shell because it could not be read
            logger.LogWarning(ex, "The user's theme could not be read; the colours follow Windows.");
            return null;
        }
    }

    private async Task<string?> ReadMyLanguageAsync()
    {
        try
        {
            using var scope = services.CreateScope();
            var me = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            if (!me.IsAuthenticated) return null;
            var language = await scope.ServiceProvider.GetRequiredService<Users.Application.Queries.GetUserLanguageQueryHandler>()
                .HandleAsync(new Users.Application.Queries.GetUserLanguageQuery(me.UserId));
            return language.IsSuccess ? language.Value : null;
        }
        catch (Exception ex)
        {
            // the language is a preference: never keep someone out of the shell because it could not be read
            logger.LogWarning(ex, "The user's language could not be read; the installation's language is used.");
            return null;
        }
    }
}
