using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tests.Common;
using Users.Application.Commands;
using Users.Application.DTOs;
using Users.Application.Queries;
using Users.Contracts.Interfaces;
using Users.Domain.Enums;
using Users.Infrastructure.DependencyInjection;
using Users.Infrastructure.Persistence;

namespace Users.Tests.Application;

public sealed class UsersApplicationTests
{
    private static Task<TestModuleDatabase<UsersDbContext>> NewDb()
        => TestModuleDatabase<UsersDbContext>.CreateAsync(s => s.AddUsersCore());

    private static Task<T> Run<T>(TestModuleDatabase<UsersDbContext> db, Func<IServiceProvider, Task<T>> action)
        => db.InScopeAsync(action);

    private static async Task<Guid> CreateUser(TestModuleDatabase<UsersDbContext> db, string username = "jane", string name = "Jane")
    {
        var r = await Run(db, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand(username, name)));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    private static async Task<Guid> CreateRole(TestModuleDatabase<UsersDbContext> db, string name = "Cashier", params string[] permissions)
    {
        var r = await Run(db, sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand(name)));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        foreach (var p in permissions)
            Assert.True((await Run(db, sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(r.Value, p)))).IsSuccess);
        return r.Value;
    }

    private static Task<UserDto?> GetUser(TestModuleDatabase<UsersDbContext> db, Guid id)
        => Run(db, sp => sp.GetRequiredService<GetUserQueryHandler>().HandleAsync(new GetUserQuery(id)));

    // ------------------------------------------------------------------ users

    [Fact]
    public async Task CreateUser_Persists_AndUsernameIsCaseInsensitivelyUnique()
    {
        await using var db = await NewDb();
        var id = await CreateUser(db, "Jane", "Jane Doe");

        var dup = await Run(db, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("JANE", "Other")));

        var dto = await GetUser(db, id);
        Assert.Equal("jane", dto!.Username);
        Assert.Equal(UserStatus.Active, dto.Status);
        Assert.Equal("Users.CreateUser.DuplicateUsername", dup.Error.Code);
        Assert.Equal(1, await Run(db, sp => sp.GetRequiredService<UsersDbContext>().Users.CountAsync()));
    }

    [Fact]
    public async Task CreateUser_InvalidInput_FailsAndPersistsNothing()
    {
        await using var db = await NewDb();

        var r = await Run(db, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("bad name", "x")));

        Assert.Equal("Users.User.UsernameInvalid", r.Error.Code);
        Assert.Equal(0, await Run(db, sp => sp.GetRequiredService<UsersDbContext>().Users.CountAsync()));
    }

    [Fact]
    public async Task UpdateDeactivateReactivate_WorkAndReportMissingUsers()
    {
        await using var db = await NewDb();
        var id = await CreateUser(db);

        Assert.True((await Run(db, sp => sp.GetRequiredService<UpdateUserCommandHandler>().HandleAsync(new UpdateUserCommand(id, "Janet", "j@x.com")))).IsSuccess);
        Assert.True((await Run(db, sp => sp.GetRequiredService<DeactivateUserCommandHandler>().HandleAsync(new DeactivateUserCommand(id)))).IsSuccess);
        Assert.Equal(UserStatus.Inactive, (await GetUser(db, id))!.Status);
        Assert.True((await Run(db, sp => sp.GetRequiredService<ReactivateUserCommandHandler>().HandleAsync(new ReactivateUserCommand(id)))).IsSuccess);

        var dto = await GetUser(db, id);
        Assert.Equal("Janet", dto!.DisplayName);
        Assert.Equal("j@x.com", dto.Email);

        var missing = Guid.NewGuid();
        Assert.Equal("Users.User.NotFound", (await Run(db, sp => sp.GetRequiredService<UpdateUserCommandHandler>().HandleAsync(new UpdateUserCommand(missing, "x", null)))).Error.Code);
        Assert.Equal("Users.User.NotFound", (await Run(db, sp => sp.GetRequiredService<DeactivateUserCommandHandler>().HandleAsync(new DeactivateUserCommand(missing)))).Error.Code);
        Assert.Equal("Users.User.NotFound", (await Run(db, sp => sp.GetRequiredService<ReactivateUserCommandHandler>().HandleAsync(new ReactivateUserCommand(missing)))).Error.Code);
    }

    [Fact]
    public async Task ListUsers_FiltersInactive_Searches_AndPages()
    {
        await using var db = await NewDb();
        await CreateUser(db, "alice", "Alice A");
        await CreateUser(db, "bob", "Bob B");
        var carol = await CreateUser(db, "carol_x", "Carol C");
        await Run(db, sp => sp.GetRequiredService<DeactivateUserCommandHandler>().HandleAsync(new DeactivateUserCommand(carol)));

        var active = await Run(db, sp => sp.GetRequiredService<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery()));
        var all = await Run(db, sp => sp.GetRequiredService<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery(IncludeInactive: true)));
        var search = await Run(db, sp => sp.GetRequiredService<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery("BO")));
        var literal = await Run(db, sp => sp.GetRequiredService<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery("_", IncludeInactive: true)));
        var paged = await Run(db, sp => sp.GetRequiredService<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery(IncludeInactive: true, Page: 2, PageSize: 2)));
        var clamped = await Run(db, sp => sp.GetRequiredService<ListUsersQueryHandler>().HandleAsync(new ListUsersQuery(Page: -4, PageSize: 99999)));

        Assert.Equal(["alice", "bob"], active.Items.Select(u => u.Username).ToArray());
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(["bob"], search.Items.Select(u => u.Username).ToArray());
        Assert.Equal(["carol_x"], literal.Items.Select(u => u.Username).ToArray());   // "_" is literal, not a wildcard
        Assert.Equal(["carol_x"], paged.Items.Select(u => u.Username).ToArray());
        Assert.Equal(3, paged.TotalCount);
        Assert.Equal(1, clamped.Page);
        Assert.Equal(ListUsersQueryHandler.MaxPageSize, clamped.PageSize);
    }

    // ------------------------------------------------------------------ roles

    [Fact]
    public async Task CreateRole_NameIsCaseInsensitivelyUnique()
    {
        await using var db = await NewDb();
        await CreateRole(db, "Cashier");

        var dup = await Run(db, sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand("CASHIER")));
        var bad = await Run(db, sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand(" ")));

        Assert.Equal("Users.CreateRole.DuplicateName", dup.Error.Code);
        Assert.Equal("Users.Role.NameRequired", bad.Error.Code);
        Assert.Single(await Run(db, sp => sp.GetRequiredService<ListRolesQueryHandler>().HandleAsync(new ListRolesQuery())));
    }

    [Fact]
    public async Task GrantAndRevokePermission_Persist()
    {
        await using var db = await NewDb();
        var role = await CreateRole(db, "Manager", "sales.refund", "inventory.adjust");

        var dup = await Run(db, sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(role, "sales.refund")));
        var revoked = await Run(db, sp => sp.GetRequiredService<RevokePermissionCommandHandler>().HandleAsync(new RevokePermissionCommand(role, "sales.refund")));
        var missing = await Run(db, sp => sp.GetRequiredService<RevokePermissionCommandHandler>().HandleAsync(new RevokePermissionCommand(role, "sales.refund")));
        var noRole = await Run(db, sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(Guid.NewGuid(), "a.b")));

        var dto = await Run(db, sp => sp.GetRequiredService<GetRoleQueryHandler>().HandleAsync(new GetRoleQuery(role)));
        Assert.Equal("Users.Role.PermissionAlreadyGranted", dup.Error.Code);
        Assert.True(revoked.IsSuccess);
        Assert.Equal("Users.Role.PermissionNotGranted", missing.Error.Code);
        Assert.Equal("Users.Role.NotFound", noRole.Error.Code);
        Assert.Equal(["inventory.adjust"], dto!.Permissions.ToArray());
        Assert.Null(await Run(db, sp => sp.GetRequiredService<GetRoleQueryHandler>().HandleAsync(new GetRoleQuery(Guid.NewGuid()))));
    }

    // ------------------------------------------------------------------ assignments + permissions

    [Fact]
    public async Task AssignRole_PersistsAndShowsOnTheUser_AndFailuresAreReported()
    {
        await using var db = await NewDb();
        var user = await CreateUser(db);
        var role = await CreateRole(db, "Cashier");

        var ok = await Run(db, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(user, role)));
        var dup = await Run(db, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(user, role)));
        var noRole = await Run(db, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(user, Guid.NewGuid())));
        var noUser = await Run(db, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(Guid.NewGuid(), role)));

        Assert.True(ok.IsSuccess);
        Assert.Equal("Users.User.RoleAlreadyAssigned", dup.Error.Code);
        Assert.Equal("Users.AssignRole.RoleNotFound", noRole.Error.Code);
        Assert.Equal("Users.User.NotFound", noUser.Error.Code);
        Assert.Equal(["Cashier"], (await GetUser(db, user))!.Roles.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task RemoveRole_Persists()
    {
        await using var db = await NewDb();
        var user = await CreateUser(db);
        var role = await CreateRole(db);
        await Run(db, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(user, role)));

        var removed = await Run(db, sp => sp.GetRequiredService<RemoveRoleCommandHandler>().HandleAsync(new RemoveRoleCommand(user, role)));
        var again = await Run(db, sp => sp.GetRequiredService<RemoveRoleCommandHandler>().HandleAsync(new RemoveRoleCommand(user, role)));

        Assert.True(removed.IsSuccess);
        Assert.Equal("Users.User.RoleNotAssigned", again.Error.Code);
        Assert.Empty((await GetUser(db, user))!.Roles);
    }

    [Fact]
    public async Task UserPermissions_AreTheDistinctUnionOfTheirRoles_AndEmptyWhenInactiveOrUnknown()
    {
        await using var db = await NewDb();
        var user = await CreateUser(db);
        var a = await CreateRole(db, "A", "sales.refund", "inventory.adjust");
        var b = await CreateRole(db, "B", "sales.refund", "reports.view");
        foreach (var role in new[] { a, b })
            await Run(db, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(user, role)));

        var perms = await Run(db, sp => sp.GetRequiredService<GetUserPermissionsQueryHandler>().HandleAsync(new GetUserPermissionsQuery(user)));
        await Run(db, sp => sp.GetRequiredService<DeactivateUserCommandHandler>().HandleAsync(new DeactivateUserCommand(user)));
        var inactive = await Run(db, sp => sp.GetRequiredService<GetUserPermissionsQueryHandler>().HandleAsync(new GetUserPermissionsQuery(user)));
        var unknown = await Run(db, sp => sp.GetRequiredService<GetUserPermissionsQueryHandler>().HandleAsync(new GetUserPermissionsQuery(Guid.NewGuid())));

        Assert.Equal(["inventory.adjust", "reports.view", "sales.refund"], perms.ToArray());
        Assert.Empty(inactive);
        Assert.Empty(unknown);
    }

    // ------------------------------------------------------------------ contracts

    [Fact]
    public async Task Contract_UserLookup_FindsByIdAndUsername_CaseInsensitively()
    {
        await using var db = await NewDb();
        var id = await CreateUser(db, "Jane.Doe", "Jane Doe");

        var byId = await Run(db, sp => sp.GetRequiredService<IUserLookup>().FindByIdAsync(id));
        var byName = await Run(db, sp => sp.GetRequiredService<IUserLookup>().FindByUsernameAsync("JANE.DOE"));
        var missing = await Run(db, sp => sp.GetRequiredService<IUserLookup>().FindByIdAsync(Guid.NewGuid()));
        var empty = await Run(db, sp => sp.GetRequiredService<IUserLookup>().FindByIdAsync(Guid.Empty));
        var badName = await Run(db, sp => sp.GetRequiredService<IUserLookup>().FindByUsernameAsync("not valid!"));

        Assert.Equal(id, byId!.UserId);
        Assert.Equal("jane.doe", byName!.Username);
        Assert.Equal("Jane Doe", byName.DisplayName);
        Assert.True(byName.IsActive);
        Assert.Null(missing);
        Assert.Null(empty);
        Assert.Null(badName);
    }

    [Fact]
    public async Task Contract_UserLookup_ReportsInactiveUsers()
    {
        await using var db = await NewDb();
        var id = await CreateUser(db);
        await Run(db, sp => sp.GetRequiredService<DeactivateUserCommandHandler>().HandleAsync(new DeactivateUserCommand(id)));

        var found = await Run(db, sp => sp.GetRequiredService<IUserLookup>().FindByIdAsync(id));

        Assert.False(found!.IsActive);
    }

    [Fact]
    public async Task Contract_PermissionChecker_ChecksThroughRoles()
    {
        await using var db = await NewDb();
        var user = await CreateUser(db);
        var role = await CreateRole(db, "Manager", "sales.refund");
        await Run(db, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(user, role)));

        var checker = await Run(db, async sp =>
        {
            var c = sp.GetRequiredService<IUserPermissionChecker>();
            return (
                Has: await c.HasPermissionAsync(user, "Sales.Refund"),
                Lacks: await c.HasPermissionAsync(user, "sales.void"),
                BadCode: await c.HasPermissionAsync(user, "not a code"),
                Unknown: await c.HasPermissionAsync(Guid.NewGuid(), "sales.refund"),
                Empty: await c.HasPermissionAsync(Guid.Empty, "sales.refund"),
                All: await c.GetPermissionsAsync(user),
                None: await c.GetPermissionsAsync(Guid.Empty));
        });

        Assert.True(checker.Has);
        Assert.False(checker.Lacks);
        Assert.False(checker.BadCode);
        Assert.False(checker.Unknown);
        Assert.False(checker.Empty);
        Assert.Equal(["sales.refund"], checker.All.ToArray());
        Assert.Empty(checker.None);
    }

    [Fact]
    public async Task Contract_DoesNotLeakDomainTypes()
    {
        Assert.DoesNotContain(typeof(IUserLookup).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Users.Domain", StringComparison.Ordinal));
        await Task.CompletedTask;
    }
}
