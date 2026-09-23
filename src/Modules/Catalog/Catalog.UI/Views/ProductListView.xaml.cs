using System.Windows;
using Catalog.UI.ViewModels;

namespace Catalog.UI.Views;

public partial class ProductListView : Window
{
    public ProductListView(ProductListViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
