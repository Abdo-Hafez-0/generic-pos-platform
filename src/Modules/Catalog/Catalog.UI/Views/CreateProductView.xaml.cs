using System.Windows;
using Catalog.UI.ViewModels;

namespace Catalog.UI.Views;

public partial class CreateProductView : Window
{
    private readonly CreateProductViewModel _viewModel;

    public CreateProductView(CreateProductViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.ProductCreated += () =>
        {
            DialogResult = true;
            Close();
        };

        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e) =>
        await _viewModel.CreateProductAsync();

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
