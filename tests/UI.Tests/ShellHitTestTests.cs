using System.Windows;
using System.Windows.Controls;
using Client.Desktop;
using Client.Desktop.Shell;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Licensing;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;

namespace UI.Tests;

/// <summary>
/// MISS-08 (found by the owner, a second time: nothing in the open screen took a click - text boxes, drop-downs, buttons - while the side
/// menu worked). The shell's "locked screen" text lay over the screen even when it was empty: a TextBlock takes the mouse over its whole
/// rectangle. HitTestTests checks each screen on its own; this checks a screen as the shell shows it, inside the real main window.
/// </summary>
public sealed class ShellHitTestTests
{
    private sealed class ControlsScreenFactory : IScreenFactory
    {
        public ScreenInstance Create(ScreenDescriptor screen) => new(WpfThread.Run(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBox { Name = "Scan", MinHeight = 52 });
            panel.Children.Add(new ComboBox { Name = "Choice", Width = 160, HorizontalAlignment = HorizontalAlignment.Left });
            panel.Children.Add(new Button { Name = "Act", Content = "Act", HorizontalAlignment = HorizontalAlignment.Left });
            return (object)panel;
        }), new FakeViewModel());
    }

    private static async Task<ShellViewModel> ShellWithOpenScreenAsync(bool licensed)
    {
        var permissions = new FakePermissions();
        permissions.Held.Add("pos.sale.create");
        var services = new ServiceCollection();
        services.AddScoped<IPermissionProvider>(_ => permissions);
        var runner = new UiActionRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        var catalog = new CapabilityCatalog([new CapabilityProvider(new CapabilityDescriptor("pos.sale.create", "pos", "Sell", "Sell"))]);
        var navigation = new NavigationBuilder(
            [new ScreenProvider(Screens.Of("pos.sell", "pos", ScreenGroups.Sales, "pos.sale.create"))], catalog, new FakeLicensing(licensed ? ["pos"] : []));

        var shell = new ShellViewModel(runner, navigation, new FakeCurrentUser(), new ControlsScreenFactory());
        await shell.RefreshAsync();
        await shell.OpenAsync(shell.Groups.SelectMany(g => g.Entries).Single());
        return shell;
    }

    private static List<string> ProblemsInTheMainWindow(ShellViewModel shell, FlowDirection direction) => WpfThread.Run(() =>
    {
        var session = new DesktopSession(new ServiceCollection().BuildServiceProvider(), new ConfigurationBuilder().Build(), NullLogger<DesktopSession>.Instance);
        var window = new MainWindow(NullLogger<MainWindow>.Instance, shell, session)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, WindowState = WindowState.Normal, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false
        };
        var content = (FrameworkElement)window.Content;
        content.FlowDirection = direction;
        window.Show();
        window.UpdateLayout();
        try
        {
            var problems = HitTestTests.Problems(content);
            foreach (var name in new[] { "Scan", "Choice", "Act" })
                if (content.FindName(name) is null && !HasDescendantNamed(content, name)) problems.Add($"the screen's control '{name}' is not shown");
            return problems;
        }
        finally { window.Close(); }
    });

    private static bool HasDescendantNamed(DependencyObject root, string name)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n, IsVisible: true } && n == name) return true;
            if (HasDescendantNamed(child, name)) return true;
        }

        return false;
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task Every_control_of_the_open_screen_is_reached_by_the_mouse_inside_the_main_window(FlowDirection direction)
    {
        var shell = await ShellWithOpenScreenAsync(licensed: true);

        var problems = ProblemsInTheMainWindow(shell, direction);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task A_locked_screen_shows_its_reason_and_the_header_and_menu_still_take_clicks()
    {
        var shell = await ShellWithOpenScreenAsync(licensed: false);

        Assert.NotNull(shell.LockedReason);
        Assert.True(shell.IsLocked);
        var problems = ProblemsInTheMainWindow(shell, FlowDirection.LeftToRight);

        Assert.Contains(problems, p => p.Contains("'Scan' is not shown"));   // no screen behind a lock
        Assert.DoesNotContain(problems, p => p.Contains("the click reaches"));
    }
}
