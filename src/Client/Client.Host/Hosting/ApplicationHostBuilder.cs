namespace Client.Host.Hosting;

/// <summary>
/// The single entry point for creating a configured IApplicationHost.
///
/// Usage (from App.xaml.cs):
///   var host = ApplicationHostBuilder
///       .Create()
///       .WithModule(new ModuleHostRegistrar())
///       .Build();
///
///   await host.StartAsync();
///
/// Design rationale:
/// - Keeps App.xaml.cs minimal and focused on WPF lifecycle
/// - Allows future stages to add modules via WithModule()
/// - The builder itself remains unaware of business logic
/// </summary>
public sealed class ApplicationHostBuilder
{
    private readonly List<IHostingModule> _modules = [];

    private ApplicationHostBuilder() { }

    /// <summary>Creates a new builder instance.</summary>
    public static ApplicationHostBuilder Create() => new();

    /// <summary>
    /// Registers a hosting module that will participate in DI configuration.
    /// </summary>
    public ApplicationHostBuilder WithModule(IHostingModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        _modules.Add(module);
        return this;
    }

    /// <summary>
    /// Builds and returns a configured IApplicationHost.
    /// Call StartAsync() on the result to actually start the host.
    /// </summary>
    public IApplicationHost Build()
    {
        return new GenericApplicationHost(_modules.AsReadOnly());
    }
}
