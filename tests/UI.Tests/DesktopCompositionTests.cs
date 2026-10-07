using System.Reflection;
using Client.Desktop;
using Client.Desktop.Shell;
using Client.Host.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;

namespace UI.Tests;

/// <summary>
/// The REAL desktop composition (platform services + <see cref="DesktopComposition"/>, as the host registers them) with nothing started: no database,
/// no window. Guards FIX-01 decision 1 for every screen any module UI declares now or later: a view model lives as long as the signed-in
/// session, so everything it takes in its constructor must be a singleton; scoped services are reached per action through the runner.
/// </summary>
public sealed class DesktopCompositionTests
{
    private static readonly Lazy<IServiceCollection> Services = new(Compose);

    private static IServiceCollection Compose()
    {
        var services = new ServiceCollection();
        var context = new HostBuilderContext(new Dictionary<object, object>())
        {
            Configuration = new ConfigurationBuilder().Build(),
            HostingEnvironment = new TestEnvironment(),
        };

        // In the order GenericApplicationHost uses: platform services first, then every hosting module.
        services.AddPlatformServices(context.Configuration);
        foreach (var module in DesktopComposition.HostingModules())
            module.RegisterServices(context, services);

        return services;
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Client.Desktop";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>The constructor ActivatorUtilities would use: the public one with the most parameters.</summary>
    private static ParameterInfo[] ConstructorOf(Type type)
        => type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First().GetParameters();

    private static IEnumerable<string> NonSingletonDependencies(Type type, IServiceCollection services)
    {
        foreach (var parameter in ConstructorOf(type))
        {
            var registrations = services.Where(d => d.ServiceType == parameter.ParameterType).ToList();
            if (registrations.Count == 0)
            {
                if (!parameter.HasDefaultValue)
                    yield return $"{type.Name}({parameter.ParameterType.Name}) is not registered";
                continue;
            }

            if (registrations.Any(d => d.Lifetime != ServiceLifetime.Singleton))
                yield return $"{type.Name}({parameter.ParameterType.Name}) is {registrations.First(d => d.Lifetime != ServiceLifetime.Singleton).Lifetime}";
        }
    }

    [Fact]
    public void The_shell_services_are_singletons()
    {
        foreach (var type in new[] { typeof(IUiActionRunner), typeof(NavigationBuilder), typeof(IScreenFactory), typeof(ShellViewModel) })
            Assert.Equal(ServiceLifetime.Singleton, Assert.Single(Services.Value, d => d.ServiceType == type).Lifetime);
    }

    [Fact]
    public void The_shell_and_every_declared_screen_view_model_depend_only_on_singletons()
    {
        using var provider = Services.Value.BuildServiceProvider();
        var screens = provider.GetServices<IScreenProvider>().SelectMany(p => p.GetScreens()).ToList();

        var violations = NonSingletonDependencies(typeof(ShellViewModel), Services.Value)
            .Concat(screens.SelectMany(s => NonSingletonDependencies(s.ViewModelType, Services.Value)))
            .ToList();

        Assert.True(violations.Count == 0, "Reach scoped services through IUiActionRunner instead:\n  " + string.Join("\n  ", violations));
    }

    private sealed class HoldsAScopedService(Platform.Application.Abstractions.Authorization.IPermissionProvider permissions)
    {
        public object Permissions => permissions;
    }

    [Fact]
    public void The_guard_reports_a_view_model_that_holds_a_scoped_service()
        => Assert.Contains("is Scoped", Assert.Single(NonSingletonDependencies(typeof(HoldsAScopedService), Services.Value)));

    [Fact]
    public void Every_declared_screen_is_valid_and_names_a_capability_some_module_declares()
    {
        using var provider = Services.Value.BuildServiceProvider();

        // Building the navigation validates every declaration (IDs, groups, views) and refuses duplicates.
        var navigation = ActivatorUtilities.CreateInstance<NavigationBuilder>(provider);
        var catalog = provider.GetRequiredService<Platform.Application.Abstractions.Authorization.ICapabilityCatalog>();

        Assert.All(navigation.Screens.Where(s => s.RequiredCapability is not null),
            s => Assert.True(catalog.Find(s.RequiredCapability!) is not null, $"Screen '{s.Id}' requires the undeclared capability '{s.RequiredCapability}'."));
    }
}
