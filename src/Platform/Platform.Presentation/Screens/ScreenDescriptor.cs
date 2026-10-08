using Platform.Application.Abstractions.Authorization;

namespace Platform.Presentation.Screens;

/// <summary>
/// One screen a module offers to the desktop shell. Declared by the module's UI project through <see cref="IScreenProvider"/>;
/// the shell creates the view and the view model, places the screen in the navigation and decides whether it is shown.
///
/// Showing a screen is a convenience, never the control: every handler behind the screen still authorizes the action itself.
/// </summary>
/// <param name="Id">Unique screen ID, lower-case dot-separated words, e.g. "pos.sell".</param>
/// <param name="Module">The owning module's manifest ID, e.g. "pos".</param>
/// <param name="Group">The navigation group, one of <see cref="ScreenGroups"/>.</param>
/// <param name="Title">Returns the localized title (evaluated when the navigation is built, so the current UI culture applies).</param>
/// <param name="ViewType">The view (a WPF user control with a parameterless constructor), created by the shell.</param>
/// <param name="ViewModelType">
/// The view model, created by the shell from the APPLICATION services. It may only depend on singletons (for example
/// <see cref="Actions.IUiActionRunner"/> and <see cref="ICurrentUser"/>): scoped services such as handlers are reached per action
/// through the runner, never held.
/// </param>
/// <param name="RequiredCapability">
/// The capability the user must hold for the screen to appear; null for a screen every signed-in user may open. When the capability
/// needs a licensed module and the license does not cover it, the screen is shown locked with an explanation.
/// </param>
/// <param name="Order">Position inside its group (ascending).</param>
public sealed record ScreenDescriptor(
    string Id,
    string Module,
    string Group,
    Func<string> Title,
    Type ViewType,
    Type ViewModelType,
    string? RequiredCapability = null,
    int Order = 0)
{
    /// <summary>Throws when the declaration is malformed (a programming error in the module UI, caught when the shell starts).</summary>
    public void Validate()
    {
        if (!CapabilityCodes.IsValid(Id))
            throw new InvalidOperationException($"'{Id}' is not a valid screen ID (lower-case dot-separated words).");

        if (string.IsNullOrWhiteSpace(Module))
            throw new InvalidOperationException($"Screen '{Id}' does not name its module.");

        if (!ScreenGroups.IsKnown(Group))
            throw new InvalidOperationException($"Screen '{Id}' uses the unknown navigation group '{Group}'.");

        if (RequiredCapability is not null && !CapabilityCodes.IsValid(RequiredCapability))
            throw new InvalidOperationException($"Screen '{Id}' requires the malformed capability '{RequiredCapability}'.");

        if (ViewType.IsAbstract || ViewType.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException($"The view of screen '{Id}' ({ViewType.Name}) needs a public parameterless constructor.");

        if (ViewModelType.IsAbstract)
            throw new InvalidOperationException($"The view model of screen '{Id}' ({ViewModelType.Name}) cannot be abstract.");
    }
}

/// <summary>Implemented once per module UI to declare the screens it offers. Registered by the desktop composition root.</summary>
public interface IScreenProvider
{
    IReadOnlyCollection<ScreenDescriptor> GetScreens();
}

/// <summary>Optional: a view model that wants to (re)load its data each time its screen is shown.</summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The screen is no longer shown: another screen was opened or the user signed out (FIX-02). A screen that listens to something
    /// outside itself (the barcode scanner) stops here. Never throws.
    /// </summary>
    Task OnNavigatedFromAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
