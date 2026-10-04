using System.Windows;
using System.Windows.Controls;
using POS.Contracts.Models;
using POS.UI.ViewModels;

namespace POS.UI.Views;

/// <summary>Minimal cashier screen. All behaviour lives in <see cref="PosViewModel"/>.</summary>
public partial class PosView : UserControl
{
    public PosView()
    {
        InitializeComponent();
        AddButton.Click += async (_, _) => await ViewModel!.AddProductAsync();
        CheckoutButton.Click += async (_, _) => await ViewModel!.CheckoutAsync();
        RemoveButton.Click += async (_, _) =>
        {
            if (CartGrid.SelectedItem is POSCartItemResult item)
                await ViewModel!.RemoveProductAsync(item.ProductId);
        };
    }

    private PosViewModel? ViewModel => DataContext as PosViewModel;
}
