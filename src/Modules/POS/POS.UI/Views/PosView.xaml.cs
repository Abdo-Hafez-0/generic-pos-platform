using System.Windows;
using System.Windows.Controls;

namespace POS.UI.Views;

/// <summary>The cashier screen. All behaviour lives in <see cref="ViewModels.PosViewModel"/>; the view only keeps the barcode box focused.</summary>
public partial class PosView : UserControl
{
    public PosView()
    {
        InitializeComponent();

        // The cashier types or scans straight away: focus the barcode box whenever the sale area becomes visible.
        SalePanel.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) Dispatcher.BeginInvoke(() => ProductCodeBox.Focus());
        };
        Loaded += (_, _) =>
        {
            if (SalePanel.IsVisible) ProductCodeBox.Focus();
        };
    }
}
