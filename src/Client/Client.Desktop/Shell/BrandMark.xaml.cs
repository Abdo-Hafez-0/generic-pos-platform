using System.Windows;
using System.Windows.Controls;

namespace Client.Desktop.Shell;

/// <summary>The Matjar logo (MISS-09) drawn in theme colours.</summary>
public partial class BrandMark : UserControl
{
    public static readonly DependencyProperty ShowNameProperty = DependencyProperty.Register(
        nameof(ShowName), typeof(bool), typeof(BrandMark), new PropertyMetadata(true, (d, e) => ((BrandMark)d).NamePanel.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public BrandMark() => InitializeComponent();

    /// <summary>False: the framed م alone.</summary>
    public bool ShowName
    {
        get => (bool)GetValue(ShowNameProperty);
        set => SetValue(ShowNameProperty, value);
    }
}
