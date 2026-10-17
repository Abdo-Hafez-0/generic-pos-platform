using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Tests.Common;
using Users.Application.Commands;
using Users.Application.Queries;
using Users.Application.Security;
using Users.Domain.Entities;
using Users.Infrastructure.DependencyInjection;
using Users.Infrastructure.Persistence;

namespace Users.Tests.Security;

/// <summary>
/// MISS-08d (owner's decision): each user has a theme - light, dark, or none to follow Windows - kept with the user like the language;
/// everyone chooses their own, another user's needs users.manage.
/// </summary>
public sealed class UserThemeTests
{
    private sealed class SignedIn : ICurrentUser
    {
        public bool IsAuthenticated => UserId != Guid.Empty;
        public Guid UserId { get; set; }
        public string UserName => "someone";
        public string DisplayName => "Someone";
    }

    private readonly SignedIn _me = new();
    private readonly global::Tests.Common.Security.ScriptedAuthorizationService _may = new(UsersCapabilities.Manage, UsersCapabilities.View);

    private Task<TestModuleDatabase<UsersDbContext>> NewDb() => TestModuleDatabase<UsersDbContext>.CreateAsync(s =>
    {
        s.AddUsersCore();
        s.AddSingleton<ICurrentUser>(_me);
        s.AddSingleton<IAuthorizationService>(_may);
    });

    private static Task<T> Run<T>(TestModuleDatabase<UsersDbContext> db, Func<IServiceProvider, Task<T>> action) => db.InScopeAsync(action);

    private static async Task<(Guid Ann, Guid Bob)> AdminAndCashierAsync(TestModuleDatabase<UsersDbContext> db)
    {
        var admins = (await Run(db, sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand("Administrator")))).Value;
        foreach (var p in new[] { UsersCapabilities.Manage, UsersCapabilities.View })
            await Run(db, sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(admins, p)));
        var ann = (await Run(db, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("ann", "Ann")))).Value;
        await Run(db, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(ann, admins)));
        var bob = (await Run(db, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("bob", "Bob")))).Value;
        return (ann, bob);
    }

    private static Task<Platform.Core.Results.Result> Set(TestModuleDatabase<UsersDbContext> db, Guid user, string? theme)
        => Run(db, sp => sp.GetRequiredService<SetUserThemeCommandHandler>().HandleAsync(new SetUserThemeCommand(user, theme)));

    private static Task<Platform.Core.Results.Result<string?>> Get(TestModuleDatabase<UsersDbContext> db, Guid user)
        => Run(db, sp => sp.GetRequiredService<GetUserThemeQueryHandler>().HandleAsync(new GetUserThemeQuery(user)));

    [Fact]
    public async Task A_cashier_chooses_their_own_theme_but_not_someone_elses()
    {
        await using var db = await NewDb();
        var (ann, bob) = await AdminAndCashierAsync(db);
        _me.UserId = bob;
        _may.Allowed.Clear();   // a cashier: no users.manage, no users.view

        Assert.Null((await Get(db, bob)).Value);   // a new user follows Windows
        Assert.True((await Set(db, bob, " Dark ")).IsSuccess);
        Assert.Equal("dark", (await Get(db, bob)).Value);
        Assert.True((await Set(db, ann, "dark")).IsFailure);
        Assert.True((await Get(db, ann)).IsFailure);

        Assert.True((await Set(db, bob, null)).IsSuccess);   // back to following Windows
        Assert.Null((await Get(db, bob)).Value);
    }

    [Fact]
    public async Task An_administrator_sets_another_users_theme()
    {
        await using var db = await NewDb();
        var (ann, bob) = await AdminAndCashierAsync(db);
        _me.UserId = ann;

        Assert.True((await Set(db, bob, "light")).IsSuccess);

        Assert.Equal("light", (await Get(db, bob)).Value);
        Assert.Equal("Users.User.NotFound", (await Set(db, Guid.NewGuid(), "dark")).Error.Code);
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("windows")]
    [InlineData("dark mode")]
    public void A_theme_is_light_or_dark_or_none(string theme)
    {
        var user = User.Create("bob", "Bob").Value;
        Assert.Equal("Users.User.ThemeInvalid", user.SetTheme(theme).Error.Code);
        Assert.Null(user.Theme);
    }
}
