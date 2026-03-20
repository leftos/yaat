using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Ground;

public partial class GroundViewWindow : Window
{
    private WindowGeometryHelper? _geometryHelper;
    private Key _alwaysOnTopKey = Key.None;
    private KeyModifiers _alwaysOnTopModifiers = KeyModifiers.None;

    public GroundViewWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm)
        {
            _geometryHelper = new WindowGeometryHelper(this, vm.Preferences, "GroundView", 800, 600);
            _geometryHelper.Restore();

            if (SettingsViewModel.ParseKeybind(vm.Preferences.AlwaysOnTopKey, out var key, out var mods))
            {
                _alwaysOnTopKey = key;
                _alwaysOnTopModifiers = mods;
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_geometryHelper is not null && e.Key == _alwaysOnTopKey && e.KeyModifiers == _alwaysOnTopModifiers)
        {
            _geometryHelper.ToggleTopmost();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
