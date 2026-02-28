using Avalonia.Controls;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class TerminalWindow : Window
{
    public TerminalWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm)
        {
            new WindowGeometryHelper(this, vm.Preferences, "Terminal", 700, 400).Restore();
        }
    }
}
