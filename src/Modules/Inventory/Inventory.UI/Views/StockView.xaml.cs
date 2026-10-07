using System.Windows.Controls;

namespace Inventory.UI.Views;

/// <summary>Stock on hand, receiving and corrections. All behaviour lives in <see cref="ViewModels.StockViewModel"/>.</summary>
public partial class StockView : UserControl
{
    public StockView() => InitializeComponent();
}
