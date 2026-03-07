using System.Collections.Specialized;
using System.Windows.Controls;
using ToneBenderController.ViewModels;

namespace ToneBenderController.Views;

public partial class WinPeBuildView : UserControl
{
    public WinPeBuildView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is WinPeBuildViewModel vm)
        {
            vm.BuildLog.CollectionChanged += OnBuildLogChanged;
        }
    }

    private void OnBuildLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => LogScroller.ScrollToEnd());
    }
}
