using System.ComponentModel;
using System.Windows;
using ToneBenderController.ViewModels;

namespace ToneBenderController.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.CleanupAsync();
    }
}
