using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class DataGridWindow : Window
{
    public DataGridWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm)
        {
            new WindowGeometryHelper(
                this, vm.Preferences, "DataGrid", 1000, 600).Restore();
        }
    }
}
