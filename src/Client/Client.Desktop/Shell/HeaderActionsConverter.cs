using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Platform.Presentation.Screens;

namespace Client.Desktop.Shell;

/// <summary>
/// MISS-08c: the open screen's main action(s) for the shell's title row - the element a view keeps in its resources under
/// <see cref="ScreenParts.HeaderActions"/>, or nothing.
/// </summary>
public sealed class HeaderActionsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is FrameworkElement view && view.Resources.Contains(ScreenParts.HeaderActions) ? view.Resources[ScreenParts.HeaderActions] : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
