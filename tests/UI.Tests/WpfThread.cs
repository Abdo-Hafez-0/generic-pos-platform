using System.Windows;
using System.Windows.Threading;
using Client.Desktop.Themes;

namespace UI.Tests;

/// <summary>
/// MISS-08: one long-lived STA thread that owns a WPF Application with the Matjar theme, as the desktop has at run time. Views use the
/// shared control styles (StaticResource) and the theme colours (DynamicResource), so they are built here, never on a thread of their own.
/// </summary>
internal static class WpfThread
{
    private static readonly Lazy<Dispatcher> Dispatcher = new(Start);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ThemeManager.Apply(app.Resources, ThemeKind.Light);
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true, Name = "WPF test thread" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    /// <summary>Runs the action on the WPF thread; an exception comes back to the test.</summary>
    public static void Run(Action action) => Dispatcher.Value.Invoke(action);

    public static T Run<T>(Func<T> action) => Dispatcher.Value.Invoke(action);
}
