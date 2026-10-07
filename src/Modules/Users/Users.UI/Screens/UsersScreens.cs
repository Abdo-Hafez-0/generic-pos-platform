using Platform.Presentation.Screens;
using Users.Application.Security;
using Users.UI.Resources;
using Users.UI.ViewModels;
using Users.UI.Views;

namespace Users.UI.Screens;

/// <summary>
/// The screens the Users module offers to the desktop shell (FIX-01e). Users needs users.view to be shown (changes need users.manage);
/// roles and permissions need users.manage. Both capabilities need no license: an installation can always be administered.
/// </summary>
public sealed class UsersScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("users.users", UsersCapabilities.Module, ScreenGroups.Administration, () => UsersText.UsersTitle,
            typeof(UsersView), typeof(UsersViewModel), UsersCapabilities.View, Order: 10),
        new ScreenDescriptor("users.roles", UsersCapabilities.Module, ScreenGroups.Administration, () => UsersText.RolesTitle,
            typeof(RolesView), typeof(RolesViewModel), UsersCapabilities.Manage, Order: 20),
    ];
}
