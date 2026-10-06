using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;

namespace Integration.Tests;

/// <summary>
/// Stage 13: logical database ownership (Rule 11) as the RUNNING composition sees it. The static rules (Architecture.Tests ARCH-SOL-019..022)
/// read migrations and sources; this reads the Entity Framework models the real host builds: a module's context that mapped another module's
/// table could write to it without any migration or SQL, and only the model shows it.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ArchitectureCompositionTests
{
    [Fact]
    public async Task EveryModuleContext_MapsOnlyItsOwnTables_AndNoTableHasTwoOwners()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        using var scope = host.Services.CreateScope();

        var modules = host.Services.GetServices<IModule>().ToList();
        var contextTypes = modules
            .SelectMany(m => m.GetType().Assembly.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(DbContext).IsAssignableFrom(t))
            .ToList();
        Assert.Equal(12, contextTypes.Count);   // every module but Reporting (no tables)

        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var physical = await host.GetTablesAsync();
        foreach (var type in contextTypes)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(type);
            var module = type.Assembly.GetName().Name!.Split('.')[0];
            var tables = context.Model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().Distinct().ToList();
            Assert.NotEmpty(tables);

            var prefix = tables[0][..(tables[0].IndexOf('_') + 1)];
            Assert.All(tables, t => Assert.True(t.StartsWith(prefix, StringComparison.Ordinal), $"{type.Name} ({module}) maps {t}, outside its prefix {prefix}"));
            foreach (var table in tables)
            {
                Assert.True(owners.TryAdd(table, module), $"{table} is mapped by {owners.GetValueOrDefault(table)} and {module}");
                Assert.Contains(table, physical);
            }
        }

        // every business table in the database has exactly one owning context (__EFMigrationsHistory and __EFMigrationsLock are EF's shared bookkeeping)
        Assert.DoesNotContain(physical, t => !t.StartsWith("__EF", StringComparison.Ordinal) && !owners.ContainsKey(t));
    }
}
