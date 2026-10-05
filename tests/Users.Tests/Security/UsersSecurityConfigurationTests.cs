using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Users.Application.Security;
using Users.Infrastructure.DependencyInjection;
using Users.Infrastructure.Security;

namespace Users.Tests.Security;

public sealed class UsersSecurityConfigurationTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddUsersSecurity(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_apply_when_nothing_is_configured()
    {
        using var provider = Build([]);

        var options = provider.GetRequiredService<UsersSecurityOptions>();
        Assert.Equal(10, options.Passwords.MinimumLength);
        Assert.Equal(5, options.Lockout.MaxFailedAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Lockout.Duration);
        Assert.Equal(PasswordHashingOptions.DefaultIterations, provider.GetRequiredService<PasswordHashingOptions>().Iterations);
    }

    [Fact]
    public void Configuration_can_make_the_rules_stricter()
    {
        using var provider = Build(new()
        {
            ["Security:Passwords:MinimumLength"] = "14",
            ["Security:Lockout:MaxFailedAttempts"] = "4",
            ["Security:Lockout:LockoutMinutes"] = "60",
            ["Security:PasswordHashing:Iterations"] = "900000"
        });

        var options = provider.GetRequiredService<UsersSecurityOptions>();
        Assert.Equal(14, options.Passwords.MinimumLength);
        Assert.Equal(4, options.Lockout.MaxFailedAttempts);
        Assert.Equal(TimeSpan.FromHours(1), options.Lockout.Duration);
        Assert.Equal(900_000, provider.GetRequiredService<PasswordHashingOptions>().Iterations);
    }

    [Fact]
    public void Configuration_can_never_weaken_the_rules_below_the_built_in_floors()
    {
        using var provider = Build(new()
        {
            ["Security:Passwords:MinimumLength"] = "1",
            ["Security:Lockout:MaxFailedAttempts"] = "100000",
            ["Security:Lockout:LockoutMinutes"] = "0",
            ["Security:PasswordHashing:Iterations"] = "1"
        });

        var options = provider.GetRequiredService<UsersSecurityOptions>();
        Assert.Equal(8, options.Passwords.MinimumLength);                                   // the 8-character floor
        Assert.Equal(TimeSpan.FromMinutes(15), options.Lockout.Duration);                   // a zero duration falls back to the default
        Assert.Equal(PasswordHashingOptions.MinimumIterations, provider.GetRequiredService<PasswordHashingOptions>().Iterations);
        // a huge attempt count is the administrator's (lenient) choice, but 1 or 2 attempts is below the floor and is raised, never lowered
        Assert.Equal(3, RaisedToFloor(1));
    }

    private static int RaisedToFloor(int configured) => new Users.Domain.ValueObjects.LockoutPolicy(configured).Normalized().MaxFailedAttempts;
}
