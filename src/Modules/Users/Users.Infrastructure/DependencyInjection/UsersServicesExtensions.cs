using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;
using Users.Application.Security;
using Users.Domain.ValueObjects;
using Users.Infrastructure.Module;
using Users.Infrastructure.Persistence;
using Users.Infrastructure.Security;

namespace Users.Infrastructure.DependencyInjection;

/// <summary>DI registration for the Users module (called by UsersHostingModule).</summary>
public static class UsersServicesExtensions
{
    public static IServiceCollection AddUsersModule(this IServiceCollection services, IConfiguration configuration)
    {
        // The same physical SQLite file as every other module DbContext.
        var dbOptions = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        var connectionString = dbOptions.BuildConnectionString();

        services.AddDbContext<UsersDbContext>(options =>
        {
            options.UseSqlite(connectionString, sqliteOptions =>
            {
                sqliteOptions.MigrationsAssembly(typeof(UsersDbContext).Assembly.FullName);
            });

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        });

        services.AddUsersSecurity(configuration);
        services.AddUsersCore();

        services.AddSingleton<IModule, UsersModule>();
        services.AddHostedService<UsersDatabaseInitializer>();
        return services;
    }

    /// <summary>
    /// Password hashing, password/lockout policy (configuration "Security:Passwords" / "Security:Lockout" / "Security:PasswordHashing", never
    /// weaker than the built-in floors) and the Users capabilities. Separate from <see cref="AddUsersCore"/> so tests choose their own cost.
    /// </summary>
    public static IServiceCollection AddUsersSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var security = configuration.GetSection("Security");
        var passwords = new PasswordPolicy(
            security.GetValue("Passwords:MinimumLength", PasswordPolicy.Default.MinimumLength),
            security.GetValue("Passwords:MaximumLength", PasswordPolicy.Default.MaximumLength));
        var lockout = new LockoutPolicy(
            security.GetValue("Lockout:MaxFailedAttempts", LockoutPolicy.Default.MaxFailedAttempts),
            TimeSpan.FromMinutes(security.GetValue("Lockout:LockoutMinutes", LockoutPolicy.DefaultDuration.TotalMinutes)));

        services.TryAddSingleton(new UsersSecurityOptions(passwords, lockout).Normalized());
        services.TryAddSingleton(PasswordHashingOptions.FromConfiguration(security.GetValue<int?>("PasswordHashing:Iterations")));
        return services;
    }

    /// <summary>The module's services without the DbContext/hosting plumbing (also used by tests with an in-memory database).</summary>
    public static IServiceCollection AddUsersCore(this IServiceCollection services)
    {
        services.AddScoped<Users.Application.Abstractions.IUsersUnitOfWork, Users.Infrastructure.Persistence.UsersUnitOfWork>();
        services.AddScoped<Users.Application.Repositories.IUserRepository, Users.Infrastructure.Repositories.EfUserRepository>();
        services.AddScoped<Users.Application.Repositories.IRoleRepository, Users.Infrastructure.Repositories.EfRoleRepository>();
        services.AddScoped<Users.Application.Repositories.IUserCredentialRepository, Users.Infrastructure.Repositories.EfUserCredentialRepository>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(UsersSecurityOptions.Default);
        services.TryAddSingleton(new PasswordHashingOptions());
        services.TryAddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ICapabilityProvider, UsersCapabilityProvider>();
        services.AddScoped<IPermissionProvider, UsersPermissionProvider>();
        services.AddScoped<Users.Contracts.Interfaces.IUserLookup, Users.Infrastructure.Services.UserLookup>();
        services.AddScoped<Users.Contracts.Interfaces.IUserPermissionChecker, Users.Infrastructure.Services.UserPermissionChecker>();
        services.AddTransient<Users.Application.Commands.SignInCommandHandler>();
        services.AddTransient<Users.Application.Commands.SignOutCommandHandler>();
        services.AddTransient<Users.Application.Commands.ChangePasswordCommandHandler>();
        services.AddTransient<Users.Application.Commands.SetUserPasswordCommandHandler>();
        services.AddTransient<Users.Application.Commands.BootstrapAdministratorCommandHandler>();
        services.AddTransient<Users.Application.Security.InteractiveSignInService>();
        services.AddTransient<Users.Application.Commands.CreateUserCommandHandler>();
        services.AddTransient<Users.Application.Commands.UpdateUserCommandHandler>();
        services.AddTransient<Users.Application.Commands.DeactivateUserCommandHandler>();
        services.AddTransient<Users.Application.Commands.ReactivateUserCommandHandler>();
        services.AddTransient<Users.Application.Commands.AssignRoleCommandHandler>();
        services.AddTransient<Users.Application.Commands.RemoveRoleCommandHandler>();
        services.AddTransient<Users.Application.Commands.CreateRoleCommandHandler>();
        services.AddTransient<Users.Application.Commands.GrantPermissionCommandHandler>();
        services.AddTransient<Users.Application.Commands.RevokePermissionCommandHandler>();
        services.AddTransient<Users.Application.Queries.GetUserQueryHandler>();
        services.AddTransient<Users.Application.Queries.ListUsersQueryHandler>();
        services.AddTransient<Users.Application.Queries.GetRoleQueryHandler>();
        services.AddTransient<Users.Application.Queries.ListRolesQueryHandler>();
        services.AddTransient<Users.Application.Queries.GetUserPermissionsQueryHandler>();
        return services;
    }
}
