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

/// <summary>FIX-13b (user decision): each user has a screen language; everyone chooses their own, another user's needs users.manage.</summary>
public sealed class UserLanguageTests
{
    private sealed class SignedIn : ICurrentUser
    {
        public bool IsAuthenticated => UserId != Guid.Empty;
        public Guid UserId { get; set; }
        public string UserName => "someone";
        public string DisplayName => "Someone";
    }

    private readonly SignedIn _me = new();

    // what the signed-in person may do (the setup below runs with users.manage/users.view; a test takes them away to act as a cashier)
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

    private static Task<Platform.Core.Results.Result> Set(TestModuleDatabase<UsersDbContext> db, Guid user, string? language)
        => Run(db, sp => sp.GetRequiredService<SetUserLanguageCommandHandler>().HandleAsync(new SetUserLanguageCommand(user, language)));

    private static Task<Platform.Core.Results.Result<string?>> Get(TestModuleDatabase<UsersDbContext> db, Guid user)
        => Run(db, sp => sp.GetRequiredService<GetUserLanguageQueryHandler>().HandleAsync(new GetUserLanguageQuery(user)));

    [Fact]
    public async Task A_cashier_chooses_their_own_language_but_not_someone_elses()
    {
        await using var db = await NewDb();
        var (ann, bob) = await AdminAndCashierAsync(db);
        _me.UserId = bob;
        _may.Allowed.Clear();   // a cashier: no users.manage, no users.view

        Assert.True((await Set(db, bob, " AR ")).IsSuccess);
        Assert.Equal("ar", (await Get(db, bob)).Value);
        Assert.True((await Set(db, ann, "ar")).IsFailure);
        Assert.True((await Get(db, ann)).IsFailure);

        Assert.True((await Set(db, bob, null)).IsSuccess);   // back to the installation's language
        Assert.Null((await Get(db, bob)).Value);
    }

    [Fact]
    public async Task An_administrator_sets_another_users_language_and_sees_it_on_the_user()
    {
        await using var db = await NewDb();
        var (ann, bob) = await AdminAndCashierAsync(db);
        _me.UserId = ann;

        Assert.True((await Set(db, bob, "ar-EG")).IsSuccess);

        Assert.Equal("ar-eg", (await Get(db, bob)).Value);
        Assert.Equal("ar-eg", (await Run(db, sp => sp.GetRequiredService<GetUserQueryHandler>().HandleAsync(new GetUserQuery(bob)))).Value!.Language);
        Assert.Equal("Users.User.NotFound", (await Set(db, Guid.NewGuid(), "ar")).Error.Code);
    }

    [Theory]
    [InlineData("arabic-language")]
    [InlineData("a r")]
    [InlineData("-ar")]
    [InlineData("ar_EG")]
    public void A_language_must_look_like_a_language_code(string language)
    {
        var user = User.Create("bob", "Bob").Value;
        Assert.Equal("Users.User.LanguageInvalid", user.SetLanguage(language).Error.Code);
        Assert.Null(user.Language);
    }
}
