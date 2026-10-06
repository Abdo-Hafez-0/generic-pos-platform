using Microsoft.Extensions.DependencyInjection;
using Users.Application.Commands;
using Users.Application.Security;

namespace Users.Tests.Security;

/// <summary>The flow the desktop's start screen drives: first-run setup, sign-in, forced password change, sign-out.</summary>
public sealed class InteractiveSignInTests
{
    private const string Password = AuthenticationTestHarness.Password;

    private static Task<T> Flow<T>(AuthenticationTestHarness h, Func<InteractiveSignInService, Task<T>> action)
        => h.Run(sp => action(sp.GetRequiredService<InteractiveSignInService>()));

    [Fact]
    public async Task A_fresh_installation_asks_for_first_run_setup_which_creates_the_administrator_and_signs_in()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();

        Assert.Equal(SignInStage.FirstRunSetup, (await Flow(h, f => f.GetStatusAsync())).Stage);

        var done = await Flow(h, f => f.CompleteFirstRunAsync("owner", "Shop Owner", Password, Password));

        Assert.True(done.IsSuccess, done.IsFailure ? done.Error.ToString() : null);
        var status = await Flow(h, f => f.GetStatusAsync());
        Assert.Equal(SignInStage.SignedIn, status.Stage);
        Assert.Equal("owner", status.UserName);
        Assert.Equal("Shop Owner", status.DisplayName);
    }

    [Fact]
    public async Task First_run_setup_needs_matching_passwords_and_respects_the_password_policy()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();

        var mismatch = await Flow(h, f => f.CompleteFirstRunAsync("owner", "Owner", Password, Password + "x"));
        var weak = await Flow(h, f => f.CompleteFirstRunAsync("owner", "Owner", "123456", "123456"));

        Assert.Equal(InteractiveSignInService.ConfirmationMismatchCode, mismatch.Error.Code);
        Assert.Equal("Users.Password.TooShort", weak.Error.Code);
        Assert.Equal(SignInStage.FirstRunSetup, (await Flow(h, f => f.GetStatusAsync())).Stage); // nothing was created
    }

    [Fact]
    public async Task Once_a_user_exists_the_start_screen_asks_for_sign_in_and_first_run_setup_is_refused()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        Assert.Equal(SignInStage.SignIn, (await Flow(h, f => f.GetStatusAsync())).Stage);
        var again = await Flow(h, f => f.CompleteFirstRunAsync("intruder", "Intruder", Password, Password));

        Assert.Equal("Users.Bootstrap.AlreadyInitialized", again.Error.Code);
        Assert.False(h.Session.IsAuthenticated);
    }

    [Fact]
    public async Task Sign_in_with_a_wrong_password_fails_and_with_the_right_one_succeeds()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, (await Flow(h, f => f.SignInAsync("admin", "wrong passphrase!!"))).Error.Code);
        Assert.True((await Flow(h, f => f.SignInAsync("admin", Password))).IsSuccess);
        Assert.Equal(SignInStage.SignedIn, (await Flow(h, f => f.GetStatusAsync())).Stage);
    }

    [Fact]
    public async Task A_temporary_password_leads_to_the_change_screen_and_then_signs_in_with_the_new_one()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);
        await h.CreateUserWithPasswordAsync("newhire", "temporary passphrase", mustChange: true);
        await Flow(h, f => f.SignOutAsync());

        var first = await Flow(h, f => f.SignInAsync("newhire", "temporary passphrase"));
        Assert.Equal(UsersSecurityErrors.PasswordChangeRequiredCode, first.Error.Code);

        var mismatch = await Flow(h, f => f.ChangePasswordAndSignInAsync("newhire", "temporary passphrase", "my own passphrase", "my own passphrasf"));
        Assert.Equal(InteractiveSignInService.ConfirmationMismatchCode, mismatch.Error.Code);

        var changed = await Flow(h, f => f.ChangePasswordAndSignInAsync("newhire", "temporary passphrase", "my own passphrase", "my own passphrase"));
        Assert.True(changed.IsSuccess, changed.IsFailure ? changed.Error.ToString() : null);
        Assert.Equal("newhire", h.Session.UserName);
    }

    [Fact]
    public async Task Signing_out_returns_the_start_screen_to_sign_in()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await Flow(h, f => f.CompleteFirstRunAsync("owner", "Owner", Password, Password));

        await Flow(h, f => f.SignOutAsync());

        Assert.Equal(SignInStage.SignIn, (await Flow(h, f => f.GetStatusAsync())).Stage);
        Assert.Contains(h.Events.Events, e => e.Action == "security.signout");
    }
}
