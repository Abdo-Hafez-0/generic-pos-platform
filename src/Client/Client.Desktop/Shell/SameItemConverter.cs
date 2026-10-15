using System.Globalization;
using System.Windows.Data;

namespace Client.Desktop.Shell;

/// <summary>MISS-08: true when both values are the same object - the menu marks the entry of the open screen.</summary>
public sealed class SameItemConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
