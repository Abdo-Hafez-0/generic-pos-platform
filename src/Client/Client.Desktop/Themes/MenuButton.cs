using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Client.Desktop.Themes;

/// <summary>
/// MISS-08c (owner's choice: actions on each row, the rest behind "⋯"): a button whose click opens its own context menu under it.
/// Set by the theme's <c>Button.RowMenu</c> style, so a module screen only writes the button and its ContextMenu. The menu's items keep
/// the row as their DataContext (a context menu takes it from the button it opens on).
/// </summary>
public static class MenuButton
{
    public static readonly DependencyProperty OpensMenuProperty = DependencyProperty.RegisterAttached(
        "OpensMenu", typeof(bool), typeof(MenuButton), new PropertyMetadata(false, OnOpensMenuChanged));

    public static bool GetOpensMenu(DependencyObject element) => (bool)element.GetValue(OpensMenuProperty);

    public static void SetOpensMenu(DependencyObject element, bool value) => element.SetValue(OpensMenuProperty, value);

    private static void OnOpensMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= Open;
        if ((bool)e.NewValue) button.Click += Open;
    }

    /// <summary>
    /// Opens the button's menu below it, reaching back toward the start side: the "⋯" sits at the end of a row, so a menu reaching outward
    /// would leave the window (in Arabic the end side is the window's left edge).
    /// </summary>
    public static void Open(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.FlowDirection = button.FlowDirection;
        menu.Placement = PlacementMode.Custom;
        var rightToLeft = button.FlowDirection == FlowDirection.RightToLeft;
        menu.CustomPopupPlacementCallback = (popup, target, _) =>
            // below the button, the menu's end edge on the button's end edge: right edges in English, left edges in Arabic (measured on the
            // real window: for a right-to-left button the offset counts from its right edge, so -width brings the menu to its left edge)
            [new CustomPopupPlacement(new Point(rightToLeft ? -target.Width : target.Width - popup.Width, target.Height), PopupPrimaryAxis.Horizontal)];
        menu.IsOpen = true;
        e.Handled = true;
    }
}
