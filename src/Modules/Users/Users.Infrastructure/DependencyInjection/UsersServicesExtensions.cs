using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Platform.Infrastructure.Persistence;
using Users.Infrastructure.Module;
using Users.Infrastructure.Persistence;

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

        services.AddUsersCore();

        services.AddSingleton<IModule, UsersModule>();
        services.AddHostedService<UsersDatabaseInitializer>();
        return services;
    }

    /// <summary>The module's services without the DbContext/hosting plumbing (also used by tests with an in-memory database).</summary>
    public static IServiceCollection AddUsersCore(this IServiceCollection services)
    {
        services.AddScoped<Users.Application.Abstractions.IUsersUnitOfWork, Users.Infrastructure.Persistence.UsersUnitOfWork>();
        services.AddScoped<Users.Application.Repositories.IUserRepository, Users.Infrastructure.Repositories.EfUserRepository>();
        services.AddScoped<Users.Application.Repositories.IRoleRepository, Users.Infrastructure.Repositories.EfRoleRepository>();
        services.AddScoped<Users.Contracts.Interfaces.IUserLookup, Users.Infrastructure.Services.UserLookup>();
        services.AddScoped<Users.Contracts.Interfaces.IUserPermissionChecker, Users.Infrastructure.Services.UserPermissionChecker>();
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
