using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Ground;

public partial class GroundViewWindow : Window
{
    public GroundViewWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm)
        {
            new WindowGeometryHelper(this, vm.Preferences, "GroundView", 800, 600).Restore();
        }
    }
}
