using Client.Host.Hosting;
using Client.Licensing.Http;
using Client.Security;
using Client.Updater.Http;
using Client.Updater.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Hosting;
using Platform.Application.Abstractions.Hardware;
using Tests.Common.Hardware;
using Tests.Common.Security;

namespace Integration.Tests;

/// <summary>
/// The network of a machine with NO Internet: every HTTP request any component tries to make is counted and fails like an unreachable
/// network would. A business operation that never touches the cloud leaves <see cref="Requests"/> at zero.
/// </summary>
public sealed class OfflineNetwork : HttpMessageHandler
{
    private int _requests;
    public int Requests => _requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        throw new HttpRequestException(HttpRequestError.ConnectionError, "Network is unreachable.", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkUnreachable));
    }
}

internal sealed class OfflineNetworkModule(OfflineNetwork network) : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.ConfigureAll<HttpClientFactoryOptions>(o => o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = network));
}

internal sealed class FakeHardwareHostingModule(FakeReceiptPrinter printer, FakeCashDrawer drawer) : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IReceiptPrinter>(printer);
        services.AddSingleton<ICashDrawer>(drawer);
    }
}

/// <summary>
/// The desktop's composition with its cloud components wired exactly as in production (licensing, license HTTP transport, updater, update HTTP
/// transport, security) on a machine whose network does not exist: a validly licensed installation, a real SQLite database, every business module,
/// fake peripherals. This is what "the shop's Internet is down" looks like to the application.
/// </summary>
internal sealed class OfflineDesktop : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    public static readonly string[] AllModules =
        ["catalog", "inventory", "sales", "pos", "customers", "suppliers", "purchasing", "pricing", "payments", "users", "audit", "cash-management", "reporting"];

    private static readonly string[] EnvironmentKeys =
    [
        "GENERICPOS_Licensing__ServerBaseUrl", "GENERICPOS_Updater__StorageDirectory", "GENERICPOS_Updater__ServerBaseUrl",
        "GENERICPOS_Licensing__AllowLegacyPlaintextIdentity"
    ];

    private readonly string _updaterFolder;
    private readonly bool _ownsLicenses;

    private OfflineDesktop(IntegrationHost host, SignedLicenseWorld licenses, bool ownsLicenses, string updaterFolder, TestClock clock, OfflineNetwork network, FakeReceiptPrinter printer, FakeCashDrawer drawer)
    {
        Host = host;
        _ownsLicenses = ownsLicenses;
        _updaterFolder = updaterFolder;
        Licenses = licenses;
        Clock = clock;
        Network = network;
        Printer = printer;
        Drawer = drawer;
    }

    public IntegrationHost Host { get; }
    public IServiceProvider Services => Host.Services;
    public SignedLicenseWorld Licenses { get; }
    public TestClock Clock { get; }
    public OfflineNetwork Network { get; }
    public FakeReceiptPrinter Printer { get; }
    public FakeCashDrawer Drawer { get; }

    public static Task<OfflineDesktop> StartAsync(string? reuseFolder = null, bool keepFiles = false, IEnumerable<IHostingModule>? extra = null)
        => StartAsync(reuseFolder, keepFiles, licenses: null, clock: null, extra);

    /// <summary>A licensed installation for desktops that are restarted: the caller owns it and disposes it after the last restart.</summary>
    public static SignedLicenseWorld NewLicenses(TestClock clock)
    {
        var licenses = new SignedLicenseWorld(Start, clock);
        licenses.Issue(Start, Start.AddDays(30), AllModules);
        return licenses;
    }

    /// <summary>Starts the offline desktop. A restart passes the previous <paramref name="licenses"/>/<paramref name="clock"/> so the same installation comes back.</summary>
    public static async Task<OfflineDesktop> StartAsync(string? reuseFolder, bool keepFiles, SignedLicenseWorld? licenses, TestClock? clock, IEnumerable<IHostingModule>? extra = null)
    {
        clock ??= new TestClock(Start);
        var ownsLicenses = licenses is null;
        if (licenses is null)
        {
            licenses = new SignedLicenseWorld(Start, clock);
            licenses.Issue(Start, Start.AddDays(30), AllModules);
        }

        var network = new OfflineNetwork();
        var printer = new FakeReceiptPrinter();
        var drawer = new FakeCashDrawer();
        var updaterFolder = Path.Combine(Path.GetTempPath(), "genericpos-upd-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentKeys[0], "https://licenses.offline.invalid/");
            Environment.SetEnvironmentVariable(EnvironmentKeys[1], updaterFolder);
            Environment.SetEnvironmentVariable(EnvironmentKeys[2], "https://updates.offline.invalid/");
            // The test license world writes a plain installation identity (like a pre-Stage-11 install); with the DPAPI protector registered, as on a real
            // desktop, it is sealed once through the documented migration switch and is protected from then on.
            Environment.SetEnvironmentVariable(EnvironmentKeys[3], "true");

            var host = await IntegrationHost.StartAsync(
                [.. IntegrationHost.CoreModules, .. IntegrationHost.Stage8Modules], reuseFolder,
                [
                    new ClientSecurityHostingModule(), .. licenses.HostModules(), new LicenseHttpHostingModule(),
                    new UpdaterHostingModule(), new UpdateHttpHostingModule(),
                    new OfflineNetworkModule(network), new FakeHardwareHostingModule(printer, drawer), .. extra ?? []
                ]);
            host.KeepFiles = keepFiles;
            return new OfflineDesktop(host, licenses, ownsLicenses, updaterFolder, clock, network, printer, drawer);
        }
        catch
        {
            ClearEnvironment();
            if (ownsLicenses) licenses.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        ClearEnvironment();
        if (_ownsLicenses) Licenses.Dispose();
        try { if (Directory.Exists(_updaterFolder)) Directory.Delete(_updaterFolder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void ClearEnvironment()
    {
        foreach (var key in EnvironmentKeys) Environment.SetEnvironmentVariable(key, null);
    }
}
