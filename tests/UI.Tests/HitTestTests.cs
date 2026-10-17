using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Platform.Presentation.Screens;

namespace UI.Tests;

/// <summary>
/// MISS-08 (found by the owner: clicking a text box did nothing, the side menu worked). The rounded text-box template kept the typing area
/// to one centred line, so on the till's tall touch boxes most of a box was frame that never took the click. Now a click anywhere inside a
/// box's frame must land in its typing area, and nothing may lie on top of any control - checked by WPF hit testing on every screen, in both
/// reading directions.
/// </summary>
public sealed class HitTestTests
{
    private static readonly Size Screen = new(1100, 760);

    /// <summary>
    /// Shows the element in a real window placed far off the screen (never visible to anyone): only a shown element is "visible" to WPF and
    /// can be hit tested like the mouse does. The caller closes the window.
    /// </summary>
    private static Window Host(FrameworkElement element, FlowDirection direction = FlowDirection.LeftToRight)
    {
        var window = new Window
        {
            Content = element, Width = Screen.Width, Height = Screen.Height, Left = -20000, Top = -20000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize
        };
        element.FlowDirection = direction;
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static bool IsWithin(DependencyObject? hit, DependencyObject target)
    {
        for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, target)) return true;
        return false;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>False when a scroll area around the control hides that point (the control is scrolled out of sight there).</summary>
    private static bool InSight(FrameworkElement control, Point point)
    {
        for (var d = VisualTreeHelper.GetParent(control); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollContentPresenter viewport)
            {
                var p = control.TranslatePoint(point, viewport);
                if (p.X < 0 || p.Y < 0 || p.X > viewport.ActualWidth || p.Y > viewport.ActualHeight) return false;
            }

        return true;
    }

    private static string Describe(object? hit) => hit is FrameworkElement fe ? $"{fe.GetType().Name} '{fe.Name}'" : hit?.GetType().Name ?? "nothing";

    /// <summary>The typing area of a text entry (the element that must take a click), or null for other controls.</summary>
    private static FrameworkElement? TypingArea(Control control) => control switch
    {
        TextBox or PasswordBox => control.Template?.FindName("PART_ContentHost", control) as FrameworkElement,
        DatePicker picker => (picker.Template?.FindName("PART_TextBox", picker) as Control) is { } box
            ? box.Template?.FindName("PART_ContentHost", box) as FrameworkElement
            : null,
        _ => null
    };

    /// <summary>Points 5 px inside a control's frame: near the top, the bottom and the start - not on the line of text.</summary>
    private static IEnumerable<Point> InsideTheFrame(FrameworkElement c)
        => [new(c.ActualWidth / 2, 5), new(c.ActualWidth / 2, c.ActualHeight - 5), new(9, c.ActualHeight / 2)];

    internal static List<string> Problems(FrameworkElement root)
    {
        var problems = new List<string>();
        foreach (var control in Descendants(root).OfType<Control>().Where(c => c is TextBox or PasswordBox or ComboBox or ButtonBase or DatePicker))
        {
            if (!control.IsVisible || !control.IsEnabled || control.ActualWidth < 12 || control.ActualHeight < 12 || control.TemplatedParent is not null) continue;
            var centre = control.TranslatePoint(new Point(control.ActualWidth / 2, control.ActualHeight / 2), root);
            if (centre.X < 0 || centre.Y < 0 || centre.X > root.ActualWidth || centre.Y > root.ActualHeight) continue;   // scrolled out of sight

            var target = (DependencyObject?)TypingArea(control) ?? control;
            var points = TypingArea(control) is null ? [new Point(control.ActualWidth / 2, control.ActualHeight / 2)] : InsideTheFrame(control);
            foreach (var point in points.Where(p => InSight(control, p)))
            {
                var hit = root.InputHitTest(control.TranslatePoint(point, root));
                if (!IsWithin(hit as DependencyObject, target))
                    problems.Add($"{control.GetType().Name} '{control.Name}' at ({point.X:0},{point.Y:0}): the click reaches {Describe(hit)}, not {(TypingArea(control) is null ? "the control" : "its typing area")}");
            }
        }

        return problems;
    }

    [Fact]
    public void A_click_anywhere_inside_a_tall_text_box_reaches_its_typing_area()
    {
        // the till's sizes: 52 px scan box, 44 px fields
        WpfThread.Run(() =>
        {
            var panel = new StackPanel { Width = 400 };
            panel.Children.Add(new TextBox { MinHeight = 52, FontSize = 18, Tag = "Scan or type a barcode" });
            panel.Children.Add(new TextBox { MinHeight = 44, Text = "12" });
            panel.Children.Add(new PasswordBox { MinHeight = 44 });
            panel.Children.Add(new DatePicker { MinHeight = 44, SelectedDate = DateTime.Today });
            var window = Host(panel);
            try
            {
                Assert.Equal(4, Descendants(panel).OfType<Control>().Count(c => TypingArea(c) is not null && c.IsVisible && c.TemplatedParent is null));   // all checked, none skipped
                Assert.Empty(Problems(panel));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Every_control_of_every_screen_is_reached_by_the_mouse_in_both_directions()
    {
        using var provider = DesktopCompositionTests.ServicesForViews();
        var screens = provider.GetServices<IScreenProvider>().SelectMany(p => p.GetScreens()).ToList();
        var problems = new List<string>();
        var checkedControls = 0;
        WpfThread.Run(() =>
        {
            foreach (var screen in screens)
                foreach (var direction in new[] { FlowDirection.LeftToRight, FlowDirection.RightToLeft })
                {
                    // as the shell builds a screen: the view bound to its real view model (hidden panels stay hidden), not loaded with data
                    var view = (FrameworkElement)Activator.CreateInstance(screen.ViewType)!;
                    view.DataContext = ActivatorUtilities.CreateInstance(provider, screen.ViewModelType);
                    var window = Host(view, direction);
                    try
                    {
                        checkedControls += Descendants(view).OfType<Control>().Count(c => c.IsVisible && c.TemplatedParent is null && (c is TextBox or PasswordBox or ComboBox or ButtonBase or DatePicker));
                        problems.AddRange(Problems(view).Select(p => $"{screen.Id} ({direction}): {p}"));
                    }
                    finally { window.Close(); }
                }
        });

        Assert.True(checkedControls > 200, $"only {checkedControls} controls were checked");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
