using System.Windows.Input;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using UI.Tests.Pos;
using Users.Application.Commands;
using Users.Application.Security;
using Users.UI.Resources;
using Users.UI.ViewModels;

namespace UI.Tests.Administration;

/// <summary>
/// FIX-01e: the users and the roles-and-permissions screens on the production-like offline desktop, signed in as its real administrator
/// (the only user who can manage users, so the lock-out rules are exercised for real).
/// </summary>
[Collection(nameof(RealDesktop))]
public sealed class UsersAndRolesOnRealDesktopTests
{
    private const string TemporaryPassword = "Temporary-Pass-2026";

    private static UiActionRunner Runner(IServiceProvider services)
        => new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);

    private static async Task Wait(ViewModelBase vm)
    {
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static async Task Run(ViewModelBase vm, ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter), "the command was not executable");
        command.Execute(parameter);
        await Wait(vm);
    }

    private static async Task<RolesViewModel> RolesScreenAsync(IServiceProvider services)
    {
        var vm = new RolesViewModel(Runner(services), services.GetRequiredService<ICapabilityCatalog>());
        await vm.OnNavigatedToAsync();
        return vm;
    }

    [Fact]
    public async Task A_new_user_gets_a_temporary_password_and_a_role_and_must_choose_a_new_password_at_sign_in()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var services = desktop.Services;

        // a Cashier role that may sell
        var roles = await RolesScreenAsync(services);
        roles.NewName = "Cashier";
        await Run(roles, roles.CreateCommand);
        Assert.Equal("Cashier", roles.SelectedRole?.Name);
        Assert.All(roles.Permissions, p => Assert.False(p.IsGranted));
        roles.Permissions.Single(p => p.Code == "pos.sale.create").IsGranted = true;   // ticked (by mouse, keyboard or automation)
        await Wait(roles);
        Assert.True(roles.Permissions.Single(p => p.Code == "pos.sale.create").IsGranted);

        var vm = new UsersViewModel(Runner(services));
        await vm.OnNavigatedToAsync();
        (vm.NewUsername, vm.NewDisplayName) = ("sara", "Sara Cashier");

        // a password the policy refuses: nothing is created
        await Run(vm, vm.CreateCommand, "short");
        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain(vm.Users, u => u.User.Username == "sara");

        await Run(vm, vm.CreateCommand, TemporaryPassword);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("sara", vm.User?.Username);
        Assert.Contains(vm.Users, u => u.User.Username == "sara");

        vm.RoleToAssign = vm.AllRoles.Single(r => r.Name == "Cashier");
        await Run(vm, vm.AssignRoleCommand);
        Assert.Equal(["Cashier"], vm.UserRoles.Select(r => r.Name));

        using var scope = services.CreateScope();
        var signIn = await scope.ServiceProvider.GetRequiredService<SignInCommandHandler>().HandleAsync(new SignInCommand("sara", TemporaryPassword));
        Assert.Equal("Users.SignIn.PasswordChangeRequired", signIn.Error.Code);   // right password, but it must be replaced first
        var permissions = await scope.ServiceProvider.GetRequiredService<IPermissionProvider>().GetPermissionsAsync(vm.User!.UserId);
        Assert.Equal(["pos.sale.create"], permissions);
    }

    [Fact]
    public async Task The_administrator_cannot_lock_everyone_out_of_user_administration()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var services = desktop.Services;
        var me = services.GetRequiredService<ICurrentUser>();

        var users = new UsersViewModel(Runner(services));
        await users.OnNavigatedToAsync();
        users.SelectedRow = users.Users.Single(u => u.User.UserId == me.UserId);
        await Wait(users);

        await Run(users, users.DeactivateCommand);   // oneself
        Assert.Contains("your own account", users.ErrorMessage);

        users.SelectedRole = users.UserRoles.Single();   // the Administrator role of the only administrator
        await Run(users, users.RemoveRoleCommand);
        Assert.Contains("nobody who can manage users", users.ErrorMessage);
        Assert.Single(users.UserRoles);

        var roles = await RolesScreenAsync(services);
        roles.SelectedRole = roles.Roles.Single(r => r.Name == users.UserRoles.Single().Name);
        roles.Permissions.Single(p => p.Code == UsersCapabilities.Manage).IsGranted = false;
        await Wait(roles);
        Assert.Contains("nobody who can manage users", roles.ErrorMessage);
        Assert.True(roles.Permissions.Single(p => p.Code == UsersCapabilities.Manage).IsGranted);   // the tick shows the stored state again
    }

    [Fact]
    public async Task A_user_is_edited_given_a_new_temporary_password_and_deactivated()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new UsersViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();
        (vm.NewUsername, vm.NewDisplayName, vm.NewEmail) = ("omar", "Omar", "omar@example.test");
        await Run(vm, vm.CreateCommand, TemporaryPassword);

        vm.EditDisplayName = "Omar Hassan";
        await Run(vm, vm.SaveDetailsCommand);
        Assert.Equal("Omar Hassan", vm.User?.DisplayName);

        await Run(vm, vm.ResetPasswordCommand, "Another-Temp-2026");
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, UsersText.PasswordSet, "Omar Hassan"), vm.StatusMessage);

        await Run(vm, vm.DeactivateCommand);
        Assert.Equal(global::Users.Domain.Enums.UserStatus.Inactive, vm.User?.Status);
        Assert.DoesNotContain(vm.Users, u => u.User.Username == "omar");   // inactive users are hidden unless asked for
        vm.ShowInactive = true;
        await Run(vm, vm.SearchCommand);
        Assert.Equal(UsersText.Inactive, vm.Users.Single(u => u.User.Username == "omar").StatusText);
        Assert.False(vm.ResetPasswordCommand.CanExecute("x"));
    }

    [Fact]
    public async Task The_administrator_gives_a_user_a_screen_language_and_can_set_it_back_to_the_installations()
    {
        // FIX-13b: the language is kept with the user and applied by the desktop when that user signs in
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new UsersViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();
        (vm.NewUsername, vm.NewDisplayName) = ("nour", "Nour");
        await Run(vm, vm.CreateCommand, TemporaryPassword);
        Assert.Equal(UsersText.LanguageDefault, vm.EditLanguage.Text);
        Assert.Equal(["", "en", "ar"], vm.Languages.Select(l => l.Code ?? "").ToArray());

        vm.EditLanguage = vm.Languages.Single(l => l.Code == "ar");
        await Run(vm, vm.SaveDetailsCommand);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal("ar", vm.User?.Language);
        Assert.Equal("العربية", vm.EditLanguage.Text);
        using (var scope = desktop.Services.CreateScope())
            Assert.Equal("ar", (await scope.ServiceProvider.GetRequiredService<global::Users.Application.Queries.GetUserLanguageQueryHandler>()
                .HandleAsync(new global::Users.Application.Queries.GetUserLanguageQuery(vm.User!.UserId))).Value);

        vm.EditLanguage = vm.Languages[0];
        await Run(vm, vm.SaveDetailsCommand);
        Assert.Null(vm.User?.Language);
    }
}
