using System.Globalization;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Licensing;
using Platform.Core.Modules;
using Platform.Presentation.Resources;

namespace Platform.Presentation.Screens;

/// <summary>Whether a screen the user may see can be opened.</summary>
public enum ScreenAvailability
{
    Available = 0,

    /// <summary>The user holds the capability, but the license does not cover the module. Shown, not opened, with a reason.</summary>
    LicenseLocked = 1
}

/// <summary>One navigation entry: the screen, whether it can be opened, and the plain reason when it cannot.</summary>
public sealed record NavigationEntry(ScreenDescriptor Screen, string Title, ScreenAvailability Availability, string? Reason)
{
    public bool IsAvailable => Availability == ScreenAvailability.Available;
}

/// <summary>A titled group of entries; empty groups are never produced.</summary>
public sealed record NavigationGroup(string Id, string Title, IReadOnlyList<NavigationEntry> Entries);

/// <summary>
/// Builds the navigation for the signed-in user from the screens the installed module UIs declare.
///
/// Rules (FIX-01 decision 2): a screen whose capability the user does not hold is HIDDEN; a screen whose capability needs a licensed
/// module that the license does not cover is SHOWN LOCKED with a plain reason; a screen without a capability is available to every
/// signed-in user; a screen naming a capability no module declared is hidden (fail closed). It is pure: the caller supplies the
/// permissions (looked up live), so a role or license change shows on the next build.
/// </summary>
public sealed class NavigationBuilder
{
    private readonly IReadOnlyList<ScreenDescriptor> _screens;
    private readonly ICapabilityCatalog _capabilities;
    private readonly ILicenseEntitlementService? _licensing;

    public NavigationBuilder(IEnumerable<IScreenProvider> providers, ICapabilityCatalog capabilities, ILicenseEntitlementService? licensing = null)
    {
        _capabilities = capabilities;
        _licensing = licensing;

        var screens = providers.SelectMany(p => p.GetScreens()).ToList();
        foreach (var screen in screens) screen.Validate();

        var duplicate = screens.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Screen '{duplicate.Key}' is declared more than once.");

        _screens = screens;
    }

    /// <summary>Every declared screen, whatever the user may do (for diagnostics and tests).</summary>
    public IReadOnlyList<ScreenDescriptor> Screens => _screens;

    public IReadOnlyList<NavigationGroup> Build(IReadOnlyCollection<string> heldPermissions)
    {
        var held = new HashSet<string>(heldPermissions, StringComparer.OrdinalIgnoreCase);

        var entries = new List<NavigationEntry>();
        foreach (var screen in _screens)
            if (Decide(screen, held) is { } entry)
                entries.Add(entry);

        return entries
            .GroupBy(e => e.Screen.Group)
            .OrderBy(g => ScreenGroups.OrderOf(g.Key))
            .Select(g => new NavigationGroup(
                g.Key,
                ScreenGroups.TitleOf(g.Key),
                g.OrderBy(e => e.Screen.Order).ThenBy(e => e.Title, StringComparer.CurrentCulture).ToList()))
            .ToList();
    }

    private NavigationEntry? Decide(ScreenDescriptor screen, HashSet<string> held)
    {
        var title = screen.Title();
        if (screen.RequiredCapability is null)
            return new NavigationEntry(screen, title, ScreenAvailability.Available, null);

        if (_capabilities.Find(screen.RequiredCapability) is not { } capability || !held.Contains(capability.Code))
            return null;

        if (_licensing is not null && capability.License == LicenseRequirement.Module && !_licensing.IsModuleLicensed(new ModuleId(capability.Module)))
            return new NavigationEntry(screen, title, ScreenAvailability.LicenseLocked,
                string.Format(CultureInfo.CurrentCulture, PresentationText.LicenseLocked, _licensing.State));

        return new NavigationEntry(screen, title, ScreenAvailability.Available, null);
    }
}
