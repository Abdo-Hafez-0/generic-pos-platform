using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Users.Application.Commands;
using Users.Application.Queries;
using Users.Application.Security;

namespace Users.Tests.Security;

/// <summary>
/// The Users handlers are called DIRECTLY here (no UI, no service wrapper): a screen that hides a button is not the control, the handler is.
/// </summary>
public sealed class UsersAuthorizationTests
{
    private const string Password = AuthenticationTestHarness.Password;

    private sealed record Setup(AuthenticationTestHarness H, Guid Admin, Guid Cashier, Guid Role) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    /// <summary>An administrator, a cashier whose role holds nothing, and a role to play with. The cashier is NOT signed in yet.</summary>
    private static async Task<Setup> NewSetupAsync()
    {
        var h = await AuthenticationTestHarness.CreateAsync();
        var admin = await h.BootstrapAdminAsync();
        await h.SignInAsync("admin", Password);
        var cashier = await h.CreateUserWithPasswordAsync("cashier");
        var role = (await h.Run(sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand("Clerk")))).Value;
        await h.Run(sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(cashier, role)));
        h.Session.SignOut();
        return new Setup(h, admin, cashier, role);
    }

    private static IEnumerable<(string Name, Func<IServiceProvider, Guid, Guid, Task<Result>> Call)> Commands() =>
    [
        ("CreateUser", (sp, user, role) => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("newbie", "New")).AsResult()),
        ("UpdateUser", (sp, user, role) => sp.GetRequiredService<UpdateUserCommandHandler>().HandleAsync(new UpdateUserCommand(user, "Changed", null))),
        ("DeactivateUser", (sp, user, role) => sp.GetRequiredService<DeactivateUserCommandHandler>().HandleAsync(new DeactivateUserCommand(user))),
        ("ReactivateUser", (sp, user, role) => sp.GetRequiredService<ReactivateUserCommandHandler>().HandleAsync(new ReactivateUserCommand(user))),
        ("AssignRole", (sp, user, role) => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(user, role))),
        ("RemoveRole", (sp, user, role) => sp.GetRequiredService<RemoveRoleCommandHandler>().HandleAsync(new RemoveRoleCommand(user, role))),
        ("CreateRole", (sp, user, role) => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand("Boss")).AsResult()),
        ("GrantPermission", (sp, user, role) => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(role, "users.manage"))),
        ("RevokePermission", (sp, user, role) => sp.GetRequiredService<RevokePermissionCommandHandler>().HandleAsync(new RevokePermissionCommand(role, "users.manage"))),
        ("SetUserPassword", (sp, user, role) => sp.GetRequiredService<SetUserPasswordCommandHandler>().HandleAsync(new SetUserPasswordCommand(user, "a fine passphrase", true)))
    ];

    public static IEnumerable<object[]> CommandNames() => Commands().Select(c => new object[] { c.Name });

    private static Func<IServiceProvider, Guid, Guid, Task<Result>> Command(string name) => Commands().Single(c => c.Name == name).Call;

    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task A_signed_in_user_without_users_manage_is_refused_by_every_command(string name)
    {
        await using var s = await NewSetupAsync();
        await s.H.SignInAsync("cashier", Password);

        var result = await s.H.Run(sp => Command(name)(sp, s.Cashier, s.Role));

        Assert.True(result.IsFailure);
        Assert.Equal(SecurityErrors.ForbiddenCode, result.Error.Code);
        Assert.Equal(ErrorType.Unauthorized, result.Error.Type);
    }

    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task Nobody_signed_in_is_refused_by_every_command(string name)
    {
        await using var s = await NewSetupAsync();

        var result = await s.H.Run(sp => Command(name)(sp, s.Cashier, s.Role));

        Assert.True(result.IsFailure);
        Assert.Equal(SecurityErrors.NotAuthenticatedCode, result.Error.Code);
    }

    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task The_administrator_gets_past_the_authorization_check(string name)
    {
        await using var s = await NewSetupAsync();
        await s.H.SignInAsync("admin", Password);

        var result = await s.H.Run(sp => Command(name)(sp, s.Cashier, s.Role));

        // Business rules may still say no (for example "already active"), but never the security layer.
        Assert.True(result.IsSuccess || result.Error.Type != ErrorType.Unauthorized, result.IsFailure ? result.Error.ToString() : null);
    }

    [Fact]
    public async Task Reading_users_and_roles_needs_users_view()
    {
        await using var s = await NewSetupAsync();
        await s.H.SignInAsync("cashier", Password);

        var denied = new Result[]
        {
            await s.H.Run(sp => sp.GetRequiredService<GetUserQueryHandler>().HandleAsync(new GetUserQuery(s.Admin))),
            await s.H.Run(sp => sp.GetRequiredService<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery())),
            await s.H.Run(sp => sp.GetRequiredService<GetRoleQueryHandler>().HandleAsync(new GetRoleQuery(s.Role))),
            await s.H.Run(sp => sp.GetRequiredService<ListRolesQueryHandler>().HandleAsync(new ListRolesQuery()))
        };
        Assert.All(denied, r => Assert.Equal(SecurityErrors.ForbiddenCode, r.Error.Code));

        s.H.Session.SignOut();
        await s.H.SignInAsync("admin", Password);
        Assert.True((await s.H.Run(sp => sp.GetRequiredService<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery()))).IsSuccess);
        Assert.NotNull((await s.H.Run(sp => sp.GetRequiredService<GetUserQueryHandler>().HandleAsync(new GetUserQuery(s.Admin)))).Value);
        Assert.NotEmpty((await s.H.Run(sp => sp.GetRequiredService<ListRolesQueryHandler>().HandleAsync(new ListRolesQuery()))).Value);
    }

    [Fact]
    public async Task Granting_a_capability_to_the_role_lets_the_user_in_on_the_next_call()
    {
        await using var s = await NewSetupAsync();
        await s.H.SignInAsync("cashier", Password);
        Assert.True((await s.H.Run(sp => sp.GetRequiredService<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery()))).IsFailure);

        // an administrator grants users.view to the Clerk role
        var session = s.H.Session;
        session.SignOut();
        await s.H.SignInAsync("admin", Password);
        await s.H.Run(sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(s.Role, UsersCapabilities.View)));
        session.SignOut();
        await s.H.SignInAsync("cashier", Password);

        Assert.True((await s.H.Run(sp => sp.GetRequiredService<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery()))).IsSuccess);
        Assert.True((await s.H.Run(sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("x1", "X")))).IsFailure);
    }

    [Fact]
    public async Task Denials_are_audited_with_the_capability_but_never_a_secret()
    {
        await using var s = await NewSetupAsync();
        await s.H.SignInAsync("cashier", Password);

        await s.H.Run(sp => sp.GetRequiredService<SetUserPasswordCommandHandler>().HandleAsync(new SetUserPasswordCommand(s.Admin, "take over the till", true)));

        var denial = Assert.Single(s.H.Events.Events, e => e.Action == "security.authorization.denied");
        Assert.Equal(UsersCapabilities.Manage, denial.SubjectId);
        Assert.Equal(s.Cashier, denial.ActorId);
        Assert.DoesNotContain("take over", s.H.Events.Dump());
    }
}

internal static class ResultTaskExtensions
{
    public static async Task<Result> AsResult<T>(this Task<Result<T>> task) => await task;
}
