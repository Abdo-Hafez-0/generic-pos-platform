using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;
using Platform.Core.Results;
using Users.Application.Commands;
using Users.Application.Security;
using Users.Domain.Entities;
using Users.Infrastructure.Security;

namespace Users.Tests.Security;

public sealed class AuthenticationTests
{
    private const string Password = AuthenticationTestHarness.Password;

    // ------------------------------------------------------------------ valid / invalid credentials

    [Fact]
    public async Task A_valid_password_signs_in_and_establishes_the_session()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        var id = await h.BootstrapAdminAsync("Admin");

        var result = await h.SignInAsync("ADMIN", Password); // user names are case-insensitive

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        Assert.Equal(id, result.Value.UserId);
        Assert.True(h.Session.IsAuthenticated);
        Assert.Equal(id, h.Session.UserId);
        Assert.Equal("admin", h.Session.UserName);
        Assert.Contains(h.Events.Events, e => e.Action == "security.signin.succeeded" && e.ActorId == id);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_no_session_exists()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        var result = await h.SignInAsync("admin", "not the password");

        Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, result.Error.Code);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
        Assert.False(h.Session.IsAuthenticated);
    }

    [Fact]
    public async Task Unknown_user_and_wrong_password_look_exactly_the_same()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        var unknown = await h.SignInAsync("nobody", Password);
        var wrong = await h.SignInAsync("admin", "wrong password!!");

        Assert.Equal(unknown.Error.Code, wrong.Error.Code);
        Assert.Equal(unknown.Error.Description, wrong.Error.Description);
    }

    [Fact]
    public async Task A_user_without_a_password_cannot_sign_in()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();
        await h.Run(sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("nopass", "No Pass")));

        var result = await h.SignInAsync("nopass", "anything at all");

        Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, result.Error.Code);
    }

    [Theory]
    [InlineData(null, "pw")]
    [InlineData("", "pw")]
    [InlineData("   ", "pw")]
    [InlineData("admin", null)]
    [InlineData("admin", "")]
    [InlineData("bad name!", "whatever")]
    [InlineData("a b", "whatever")]
    public async Task Malformed_credentials_are_refused_with_the_generic_error(string? username, string? password)
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        var result = await h.SignInAsync(username!, password!);

        Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, result.Error.Code);
        Assert.False(h.Session.IsAuthenticated);
    }

    [Fact]
    public async Task Oversized_input_is_refused_before_any_hashing()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        var result = await h.SignInAsync("admin", new string('p', 10_000));

        Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, result.Error.Code);
        var credential = await h.Query(db => db.Credentials.SingleAsync());
        Assert.Equal(0, credential.FailedAttempts); // refused up front: it does not even count as a guess
    }

    [Fact]
    public async Task A_disabled_user_cannot_sign_in_even_with_the_right_password()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        var adminId = await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);
        var cashier = await h.CreateUserWithPasswordAsync("cashier");
        h.Session.SignOut();
        await h.Run(sp => sp.GetRequiredService<DeactivateUserCommandHandler>().HandleAsync(new DeactivateUserCommand(cashier)));

        var right = await h.SignInAsync("cashier", Password);
        var wrong = await h.SignInAsync("cashier", "wrong password!!");

        Assert.Equal(UsersSecurityErrors.AccountDisabledCode, right.Error.Code);
        Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, wrong.Error.Code); // the reason is only revealed to someone who knows the password
        Assert.False(h.Session.IsAuthenticated);
        Assert.NotEqual(adminId, cashier);
    }

    // ------------------------------------------------------------------ failed-login handling

    [Fact]
    public async Task Repeated_failures_lock_the_account_even_against_the_right_password()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync(
            new UsersSecurityOptions(Users.Domain.ValueObjects.PasswordPolicy.Default, AuthenticationTestHarness.QuickLockout(3, 10)));
        await h.BootstrapAdminAsync();

        await h.SignInAsync("admin", "wrong one!!!");
        await h.SignInAsync("admin", "wrong two!!!");
        var third = await h.SignInAsync("admin", "wrong three!!");
        var correct = await h.SignInAsync("admin", Password);

        Assert.Equal(UsersSecurityErrors.LockedOutCode, third.Error.Code);
        Assert.Equal(UsersSecurityErrors.LockedOutCode, correct.Error.Code);
        Assert.False(h.Session.IsAuthenticated);
        Assert.Contains(h.Events.Events, e => e.Action == "security.signin.locked");
        Assert.Contains(h.Events.Events, e => e.Action == "security.signin.blocked");
    }

    [Fact]
    public async Task The_lock_ends_after_its_duration()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync(
            new UsersSecurityOptions(Users.Domain.ValueObjects.PasswordPolicy.Default, AuthenticationTestHarness.QuickLockout(3, 10)));
        await h.BootstrapAdminAsync();
        for (var i = 0; i < 3; i++) await h.SignInAsync("admin", "wrong guess!!");

        h.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(UsersSecurityErrors.LockedOutCode, (await h.SignInAsync("admin", Password)).Error.Code);

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True((await h.SignInAsync("admin", Password)).IsSuccess);
    }

    [Fact]
    public async Task A_success_resets_the_failure_count()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync(
            new UsersSecurityOptions(Users.Domain.ValueObjects.PasswordPolicy.Default, AuthenticationTestHarness.QuickLockout(3, 10)));
        await h.BootstrapAdminAsync();

        await h.SignInAsync("admin", "wrong one!!!");
        await h.SignInAsync("admin", "wrong two!!!");
        Assert.True((await h.SignInAsync("admin", Password)).IsSuccess);
        await h.SignInAsync("admin", "wrong again!!");
        await h.SignInAsync("admin", "wrong again!!");

        Assert.True((await h.SignInAsync("admin", Password)).IsSuccess); // two fresh failures are not three
    }

    [Fact]
    public async Task Failed_attempts_are_audited_without_the_password_or_a_mistyped_username()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        await h.SignInAsync("admin", "hunter2-hunter2");
        await h.SignInAsync("hunter2-in-the-name-box", "whatever pw");

        var dump = h.Events.Dump();
        Assert.Contains("security.signin.failed", dump);
        Assert.DoesNotContain("hunter2", dump);
        Assert.DoesNotContain(Password, dump);
    }

    // ------------------------------------------------------------------ password storage

    [Fact]
    public async Task Only_a_hash_is_stored_never_the_password()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        var credential = await h.Query(db => db.Credentials.AsNoTracking().SingleAsync());

        Assert.StartsWith("PBKDF2-SHA256$", credential.PasswordHash);
        Assert.DoesNotContain(Password, credential.PasswordHash);
    }

    [Fact]
    public async Task A_hash_from_an_older_cost_is_upgraded_on_the_next_successful_sign_in()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync(hashIterations: 2000);
        await h.BootstrapAdminAsync();
        var weaker = new Pbkdf2PasswordHasher(new PasswordHashingOptions(500)).Hash(Password);
        await h.Run(async sp =>
        {
            var db = sp.GetRequiredService<Users.Infrastructure.Persistence.UsersDbContext>();
            (await db.Credentials.SingleAsync()).UpgradeHash(weaker);
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.True((await h.SignInAsync("admin", Password)).IsSuccess);

        var stored = (await h.Query(db => db.Credentials.AsNoTracking().SingleAsync())).PasswordHash;
        Assert.StartsWith("PBKDF2-SHA256$2000$", stored);
        Assert.True((await h.SignInAsync("admin", Password)).IsSuccess);
    }

    // ------------------------------------------------------------------ password change

    [Fact]
    public async Task A_user_changes_their_own_password_and_the_old_one_stops_working()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        var changed = await h.Run(sp => sp.GetRequiredService<ChangePasswordCommandHandler>()
            .HandleAsync(new ChangePasswordCommand("admin", Password, "a brand new passphrase")));

        Assert.True(changed.IsSuccess, changed.IsFailure ? changed.Error.ToString() : null);
        Assert.True((await h.SignInAsync("admin", "a brand new passphrase")).IsSuccess);
        h.Session.SignOut();
        Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, (await h.SignInAsync("admin", Password)).Error.Code);
        Assert.Contains(h.Events.Events, e => e.Action == "security.password.changed");
        Assert.DoesNotContain("brand new", h.Events.Dump());
    }

    [Fact]
    public async Task Changing_a_password_needs_the_right_current_password_and_counts_failures()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync(
            new UsersSecurityOptions(Users.Domain.ValueObjects.PasswordPolicy.Default, AuthenticationTestHarness.QuickLockout(3, 10)));
        await h.BootstrapAdminAsync();

        Task<Result> Change(string current) => h.Run(sp => sp.GetRequiredService<ChangePasswordCommandHandler>()
            .HandleAsync(new ChangePasswordCommand("admin", current, "a brand new passphrase")));

        Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, (await Change("wrong one!!!")).Error.Code);
        Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, (await Change("wrong two!!!")).Error.Code);
        Assert.Equal(UsersSecurityErrors.LockedOutCode, (await Change("wrong three!!")).Error.Code);
        Assert.Equal(UsersSecurityErrors.LockedOutCode, (await h.SignInAsync("admin", Password)).Error.Code); // password change is not a way around lockout
    }

    [Theory]
    [InlineData("short", "Users.Password.TooShort")]
    [InlineData("Password123", "Users.Password.TooCommon")]
    [InlineData("my-admin-passphrase", "Users.Password.ContainsUsername")]
    [InlineData("correct horse battery", "Users.Password.Unchanged")]
    public async Task A_weak_or_unchanged_new_password_is_refused(string newPassword, string code)
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        var result = await h.Run(sp => sp.GetRequiredService<ChangePasswordCommandHandler>()
            .HandleAsync(new ChangePasswordCommand("admin", Password, newPassword)));

        Assert.Equal(code, result.Error.Code);
        Assert.True((await h.SignInAsync("admin", Password)).IsSuccess); // nothing changed
    }

    [Fact]
    public async Task A_temporary_password_must_be_replaced_before_the_first_sign_in()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);
        await h.CreateUserWithPasswordAsync("newhire", "temporary passphrase", mustChange: true);
        h.Session.SignOut();

        var blocked = await h.SignInAsync("newhire", "temporary passphrase");
        Assert.Equal(UsersSecurityErrors.PasswordChangeRequiredCode, blocked.Error.Code);
        Assert.False(h.Session.IsAuthenticated);

        var changed = await h.Run(sp => sp.GetRequiredService<ChangePasswordCommandHandler>()
            .HandleAsync(new ChangePasswordCommand("newhire", "temporary passphrase", "my own new passphrase")));
        Assert.True(changed.IsSuccess, changed.IsFailure ? changed.Error.ToString() : null);

        Assert.True((await h.SignInAsync("newhire", "my own new passphrase")).IsSuccess);
    }

    // ------------------------------------------------------------------ administrative password reset

    [Fact]
    public async Task An_administrator_reset_unlocks_the_account()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync(
            new UsersSecurityOptions(Users.Domain.ValueObjects.PasswordPolicy.Default, AuthenticationTestHarness.QuickLockout(3, 60)));
        await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);
        var cashier = await h.CreateUserWithPasswordAsync("cashier");
        h.Session.SignOut();
        for (var i = 0; i < 3; i++) await h.SignInAsync("cashier", "wrong guess!!");
        Assert.Equal(UsersSecurityErrors.LockedOutCode, (await h.SignInAsync("cashier", Password)).Error.Code);

        await h.SignInAsync("admin", Password);
        var reset = await h.Run(sp => sp.GetRequiredService<SetUserPasswordCommandHandler>()
            .HandleAsync(new SetUserPasswordCommand(cashier, "fresh temporary one", MustChangeOnNextSignIn: false)));
        h.Session.SignOut();

        Assert.True(reset.IsSuccess);
        Assert.True((await h.SignInAsync("cashier", "fresh temporary one")).IsSuccess);
        Assert.Contains(h.Events.Events, e => e.Action == "security.password.reset" && e.SubjectId == cashier.ToString());
        Assert.DoesNotContain("fresh temporary", h.Events.Dump());
    }

    [Fact]
    public async Task Setting_a_password_requires_the_users_manage_capability()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);
        var cashier = await h.CreateUserWithPasswordAsync("cashier");
        var victim = await h.CreateUserWithPasswordAsync("victim");
        h.Session.SignOut();
        await h.SignInAsync("cashier", Password);

        var result = await h.Run(sp => sp.GetRequiredService<SetUserPasswordCommandHandler>()
            .HandleAsync(new SetUserPasswordCommand(victim, "i am taking over", false)));

        Assert.Equal(SecurityErrors.ForbiddenCode, result.Error.Code);
        h.Session.SignOut();
        Assert.True((await h.SignInAsync("victim", Password)).IsSuccess); // the victim's password was not changed
        Assert.NotEqual(cashier, victim);
    }

    [Fact]
    public async Task Setting_a_password_for_an_unknown_user_or_with_a_weak_password_fails()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);
        var cashier = await h.CreateUserWithPasswordAsync("cashier");

        var unknown = await h.Run(sp => sp.GetRequiredService<SetUserPasswordCommandHandler>()
            .HandleAsync(new SetUserPasswordCommand(Guid.NewGuid(), "a fine passphrase", true)));
        var weak = await h.Run(sp => sp.GetRequiredService<SetUserPasswordCommandHandler>()
            .HandleAsync(new SetUserPasswordCommand(cashier, "123456", true)));

        Assert.Equal("Users.User.NotFound", unknown.Error.Code);
        Assert.Equal("Users.Password.TooShort", weak.Error.Code);
    }

    // ------------------------------------------------------------------ sign out

    [Fact]
    public async Task Signing_out_ends_the_session_and_is_audited()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);

        var result = await h.Run(sp => sp.GetRequiredService<SignOutCommandHandler>().HandleAsync(new SignOutCommand()));

        Assert.True(result.IsSuccess);
        Assert.False(h.Session.IsAuthenticated);
        Assert.Contains(h.Events.Events, e => e.Action == "security.signout");
        Assert.True((await h.Run(sp => sp.GetRequiredService<SignOutCommandHandler>().HandleAsync(new SignOutCommand()))).IsSuccess);
    }

    // ------------------------------------------------------------------ first-run administrator

    [Fact]
    public async Task The_first_administrator_holds_every_declared_capability_and_can_sign_in()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        var id = await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);

        var catalog = await h.Run(sp => Task.FromResult(sp.GetRequiredService<ICapabilityCatalog>()));
        var authorization = await h.Run(sp => Task.FromResult(sp.GetRequiredService<IAuthorizationService>()));

        Assert.NotEmpty(catalog.All);
        foreach (var capability in catalog.All)
            Assert.True(await h.Run(sp => sp.GetRequiredService<IAuthorizationService>().IsAllowedAsync(capability.Code)), capability.Code);
        Assert.NotNull(authorization);
        Assert.Contains(h.Events.Events, e => e.Action == "security.bootstrap.administrator-created" && e.ActorId == id);
    }

    [Fact]
    public async Task First_run_setup_works_once_and_is_then_refused_forever()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();

        var second = await h.Run(sp => sp.GetRequiredService<BootstrapAdministratorCommandHandler>()
            .HandleAsync(new BootstrapAdministratorCommand("intruder", "Intruder", Password)));

        Assert.Equal("Users.Bootstrap.AlreadyInitialized", second.Error.Code);
        Assert.Equal(1, await h.Query(db => db.Users.CountAsync()));
        Assert.Contains(h.Events.Events, e => e.Action == "security.bootstrap.refused");
    }

    [Fact]
    public async Task First_run_setup_refuses_a_weak_password_and_stores_nothing()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();

        var result = await h.Run(sp => sp.GetRequiredService<BootstrapAdministratorCommandHandler>()
            .HandleAsync(new BootstrapAdministratorCommand("admin", "Admin", "admin")));

        Assert.Equal("Users.Password.TooShort", result.Error.Code);
        Assert.Equal(0, await h.Query(db => db.Users.CountAsync()));
        Assert.Equal(0, await h.Query(db => db.Roles.CountAsync()));
    }

    // ------------------------------------------------------------------ live permissions

    [Fact]
    public async Task Role_changes_and_deactivation_take_effect_on_the_next_decision()
    {
        await using var h = await AuthenticationTestHarness.CreateAsync();
        await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);
        var cashier = await h.CreateUserWithPasswordAsync("cashier");
        var roleId = (await h.Run(sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand("Viewer")))).Value;
        await h.Run(sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(cashier, roleId)));
        h.Session.SignOut();
        await h.SignInAsync("cashier", Password);

        Task<bool> Allowed() => h.Run(sp => sp.GetRequiredService<IAuthorizationService>().IsAllowedAsync(UsersCapabilities.View));

        Assert.False(await Allowed());

        // an administrator grants the capability while the cashier stays signed in
        var session = h.Session;
        session.SignOut();
        await h.SignInAsync("admin", Password);
        await h.Run(sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(roleId, UsersCapabilities.View)));
        session.SignOut();
        await h.SignInAsync("cashier", Password);
        Assert.True(await Allowed());

        // ... and revokes it again
        session.SignOut();
        await h.SignInAsync("admin", Password);
        await h.Run(sp => sp.GetRequiredService<RevokePermissionCommandHandler>().HandleAsync(new RevokePermissionCommand(roleId, UsersCapabilities.View)));
        session.SignOut();
        await h.SignInAsync("cashier", Password);
        Assert.False(await Allowed());

        // deactivating the user (session still open) removes everything immediately
        session.SignOut();
        await h.SignInAsync("admin", Password);
        await h.Run(sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(roleId, UsersCapabilities.View)));
        session.SignOut();
        await h.SignInAsync("cashier", Password);
        Assert.True(await Allowed());
        await h.Run(async sp =>
        {
            var db = sp.GetRequiredService<Users.Infrastructure.Persistence.UsersDbContext>();
            (await db.Users.SingleAsync(u => u.Username == "cashier")).Deactivate();
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.True(h.Session.IsAuthenticated);
        Assert.False(await Allowed());
    }
}
