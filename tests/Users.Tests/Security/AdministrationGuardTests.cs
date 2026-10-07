using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Tests.Common;
using Users.Application.Commands;
using Users.Application.Queries;
using Users.Application.Security;
using Users.Domain.Enums;
using Users.Infrastructure.DependencyInjection;
using Users.Infrastructure.Persistence;

namespace Users.Tests.Security;

/// <summary>FIX-01e (user decision): user administration can never lock itself out, and nobody deactivates their own account.</summary>
public sealed class AdministrationGuardTests
{
    private sealed class SignedIn : ICurrentUser
    {
        public bool IsAuthenticated => UserId != Guid.Empty;
        public Guid UserId { get; set; }
        public string UserName => "admin";
        public string DisplayName => "Admin";
    }

    private readonly SignedIn _me = new();

    private Task<TestModuleDatabase<UsersDbContext>> NewDb() => TestModuleDatabase<UsersDbContext>.CreateAsync(s =>
    {
        s.AddUsersCore();
        s.AddSingleton<ICurrentUser>(_me);
    });

    private static Task<T> Run<T>(TestModuleDatabase<UsersDbContext> db, Func<IServiceProvider, Task<T>> action) => db.InScopeAsync(action);

    private static async Task<Guid> User(TestModuleDatabase<UsersDbContext> db, string name, Guid? role = null)
    {
        var id = (await Run(db, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand(name, name)))).Value;
        if (role is { } r) Assert.True((await Run(db, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(id, r)))).IsSuccess);
        return id;
    }

    private static async Task<Guid> Role(TestModuleDatabase<UsersDbContext> db, string name, params string[] permissions)
    {
        var id = (await Run(db, sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand(name)))).Value;
        foreach (var p in permissions)
            Assert.True((await Run(db, sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(id, p)))).IsSuccess);
        return id;
    }

    private static Task<Result> Deactivate(TestModuleDatabase<UsersDbContext> db, Guid user)
        => Run(db, sp => sp.GetRequiredService<DeactivateUserCommandHandler>().HandleAsync(new DeactivateUserCommand(user)));

    private static Task<Result> RemoveRole(TestModuleDatabase<UsersDbContext> db, Guid user, Guid role)
        => Run(db, sp => sp.GetRequiredService<RemoveRoleCommandHandler>().HandleAsync(new RemoveRoleCommand(user, role)));

    private static Task<Result> Revoke(TestModuleDatabase<UsersDbContext> db, Guid role, string permission)
        => Run(db, sp => sp.GetRequiredService<RevokePermissionCommandHandler>().HandleAsync(new RevokePermissionCommand(role, permission)));

    [Fact]
    public async Task The_last_user_who_can_manage_users_cannot_be_deactivated_but_anyone_else_can()
    {
        await using var db = await NewDb();
        var admins = await Role(db, "Administrator", UsersCapabilities.Manage, UsersCapabilities.View);
        var cashiers = await Role(db, "Cashier", "pos.sale.create");
        var ann = await User(db, "ann", admins);
        var bob = await User(db, "bob", cashiers);
        _me.UserId = bob;   // someone else is signed in

        var refused = await Deactivate(db, ann);
        Assert.Equal("Users.LastAdministrator", refused.Error.Code);
        Assert.Contains("nobody who can manage users", refused.Error.Description);

        _me.UserId = ann;
        Assert.True((await Deactivate(db, bob)).IsSuccess);

        var carl = await User(db, "carl", admins);
        _me.UserId = carl;
        Assert.True((await Deactivate(db, ann)).IsSuccess);   // carl can still manage users
    }

    [Fact]
    public async Task Nobody_can_deactivate_their_own_account()
    {
        await using var db = await NewDb();
        var admins = await Role(db, "Administrator", UsersCapabilities.Manage);
        var ann = await User(db, "ann", admins);
        await User(db, "carl", admins);
        _me.UserId = ann;

        var refused = await Deactivate(db, ann);

        Assert.Equal("Users.SelfDeactivation", refused.Error.Code);
        var after = await Run(db, sp => sp.GetRequiredService<GetUserQueryHandler>().HandleAsync(new GetUserQuery(ann)));
        Assert.Equal(UserStatus.Active, after.Value!.Status);
    }

    [Fact]
    public async Task Taking_the_last_managing_role_away_is_refused_while_another_manager_makes_it_allowed()
    {
        await using var db = await NewDb();
        var admins = await Role(db, "Administrator", UsersCapabilities.Manage);
        var ann = await User(db, "ann", admins);

        Assert.Equal("Users.LastAdministrator", (await RemoveRole(db, ann, admins)).Error.Code);

        await User(db, "carl", admins);
        Assert.True((await RemoveRole(db, ann, admins)).IsSuccess);
    }

    [Fact]
    public async Task Revoking_users_manage_from_the_only_role_that_grants_it_is_refused_other_permissions_are_not_affected()
    {
        await using var db = await NewDb();
        var admins = await Role(db, "Administrator", UsersCapabilities.Manage, "pos.sale.create");
        await User(db, "ann", admins);

        Assert.Equal("Users.LastAdministrator", (await Revoke(db, admins, "USERS.MANAGE")).Error.Code);
        Assert.True((await Revoke(db, admins, "pos.sale.create")).IsSuccess);

        var backup = await Role(db, "Backup admin", UsersCapabilities.Manage);
        await User(db, "carl", backup);
        Assert.True((await Revoke(db, admins, UsersCapabilities.Manage)).IsSuccess);
    }

    [Fact]
    public async Task An_inactive_holder_does_not_count_as_someone_who_can_manage_users()
    {
        await using var db = await NewDb();
        var admins = await Role(db, "Administrator", UsersCapabilities.Manage);
        var ann = await User(db, "ann", admins);
        var carl = await User(db, "carl", admins);
        _me.UserId = ann;
        Assert.True((await Deactivate(db, carl)).IsSuccess);

        Assert.Equal("Users.LastAdministrator", (await RemoveRole(db, ann, admins)).Error.Code);
    }
}
