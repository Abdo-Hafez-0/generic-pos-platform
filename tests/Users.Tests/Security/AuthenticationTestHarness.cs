using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;
using Tests.Common;
using Tests.Common.Security;
using Users.Application.Commands;
using Users.Application.Security;
using Users.Domain.ValueObjects;
using Users.Infrastructure.DependencyInjection;
using Users.Infrastructure.Persistence;
using Users.Infrastructure.Security;

namespace Users.Tests.Security;

/// <summary>
/// The Users module on an in-memory database with the REAL authorization service, session, hashing and lockout rules; only the cost of
/// hashing is lowered so the suite stays fast. Time is manual so lockout can be tested without waiting.
/// </summary>
internal sealed class AuthenticationTestHarness : IAsyncDisposable
{
    public const string Password = "correct horse battery";

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));

    public RecordingSecurityEventSink Events { get; } = new();

    public SessionContext Session { get; } = new();

    public TestModuleDatabase<UsersDbContext> Db { get; private set; } = null!;

    public static async Task<AuthenticationTestHarness> CreateAsync(
        UsersSecurityOptions? options = null, int hashIterations = 1000, Action<IServiceCollection>? extra = null)
    {
        var harness = new AuthenticationTestHarness();
        harness.Db = await TestModuleDatabase<UsersDbContext>.CreateAsync(s =>
        {
            s.AddSingleton<TimeProvider>(harness.Clock);
            s.AddSingleton(options ?? UsersSecurityOptions.Default);
            s.AddSingleton(new PasswordHashingOptions(hashIterations));
            s.AddSingleton(harness.Session);
            s.AddSingleton<ICurrentUser>(harness.Session);
            s.AddSingleton<ISessionManager>(harness.Session);
            s.AddSingleton<ISecurityEventSink>(harness.Events);
            s.AddUsersCore();
            s.AddSingleton<ICapabilityCatalog>(sp => new CapabilityCatalog(sp.GetServices<ICapabilityProvider>()));
            s.AddScoped<IAuthorizationService, AuthorizationService>();
            extra?.Invoke(s);
        });
        return harness;
    }

    public Task<T> Run<T>(Func<IServiceProvider, Task<T>> action) => Db.InScopeAsync(action);

    /// <summary>Creates the first administrator (the only way to get started) and returns the user ID.</summary>
    public async Task<Guid> BootstrapAdminAsync(string username = "admin", string password = Password)
    {
        var r = await Run(sp => sp.GetRequiredService<BootstrapAdministratorCommandHandler>()
            .HandleAsync(new BootstrapAdministratorCommand(username, "Administrator", password)));
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    public Task<Platform.Core.Results.Result<SignInOutcome>> SignInAsync(string username, string password)
        => Run(sp => sp.GetRequiredService<SignInCommandHandler>().HandleAsync(new SignInCommand(username, password)));

    /// <summary>Creates an ordinary user with a working (non-temporary) password. Needs an administrator to be signed in.</summary>
    public async Task<Guid> CreateUserWithPasswordAsync(string username, string password = Password, bool mustChange = false)
    {
        var id = await Run(async sp =>
        {
            var created = await sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand(username, username));
            Assert.True(created.IsSuccess, created.IsFailure ? created.Error.ToString() : null);
            return created.Value;
        });
        var set = await Run(sp => sp.GetRequiredService<SetUserPasswordCommandHandler>()
            .HandleAsync(new SetUserPasswordCommand(id, password, mustChange)));
        Assert.True(set.IsSuccess, set.IsFailure ? set.Error.ToString() : null);
        return id;
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();

    public Task<T> Query<T>(Func<UsersDbContext, Task<T>> query)
        => Run(sp => query(sp.GetRequiredService<UsersDbContext>()));

    public static LockoutPolicy QuickLockout(int attempts = 3, int minutes = 10) => new(attempts, TimeSpan.FromMinutes(minutes));
}
