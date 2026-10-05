using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Tests.Common;

/// <summary>
/// An isolated in-memory SQLite database for ONE module DbContext plus that module's core services
/// (repositories, unit of work, handlers, contract implementations), exactly as the module registers them.
/// The schema is created from the EF model (EnsureCreated); migrations are tested separately.
/// Cross-module contracts are supplied by the test as stubs through <c>register</c>.
/// </summary>
public sealed class TestModuleDatabase<TContext> : IAsyncDisposable where TContext : DbContext
{
    private readonly ServiceProvider _provider;

    private TestModuleDatabase(ServiceProvider provider) => _provider = provider;

    public static async Task<TestModuleDatabase<TContext>> CreateAsync(Action<IServiceCollection> register)
    {
        var services = new ServiceCollection();
        var connection = $"Data Source=file:test-{typeof(TContext).Name}-{Guid.NewGuid():N}?mode=memory&cache=shared";
        services.AddDbContext<TContext>(o =>
        {
            o.UseSqlite(connection);
            o.EnableSensitiveDataLogging();
        });
        // Business-behaviour tests are not about security: authorization allows everything unless the test registers a real or scripted one.
        services.AddSingleton<Platform.Application.Abstractions.Authorization.IAuthorizationService, Security.AllowAllAuthorizationService>();
        register(services);

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.EnsureCreatedAsync();
        return new TestModuleDatabase<TContext>(provider);
    }

    public IServiceScope CreateScope() => _provider.CreateScope();

    /// <summary>Runs <paramref name="action"/> in a fresh scope (a fresh DbContext), like a separate request.</summary>
    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _provider.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}

public static class RepoPaths
{
    /// <summary>The repository root (folder containing GenericPOS.sln).</summary>
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GenericPOS.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("GenericPOS.sln not found above the test output directory.");
    }
}
