using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar;

public partial class RadarViewWindow : Window
{
    public RadarViewWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm)
        {
            new WindowGeometryHelper(this, vm.Preferences, "RadarView", 800, 600).Restore();
        }
    }
}
