using Avalonia.Controls;
using Avalonia.Input;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class TerminalWindow : Window
{
    private WindowGeometryHelper? _geometryHelper;
    private Key _alwaysOnTopKey = Key.None;
    private KeyModifiers _alwaysOnTopModifiers = KeyModifiers.None;

    public TerminalWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm)
        {
            _geometryHelper = new WindowGeometryHelper(this, vm.Preferences, "Terminal", 700, 400);
            _geometryHelper.Restore();

            if (SettingsViewModel.ParseKeybind(vm.Preferences.AlwaysOnTopKey, out var aotKey, out var aotMods))
            {
                _alwaysOnTopKey = aotKey;
                _alwaysOnTopModifiers = aotMods;
            }

            var cmdView = this.FindControl<CommandInputView>("CommandInputView");
            if (cmdView is not null && SettingsViewModel.ParseKeybind(vm.Preferences.AircraftSelectKey, out var key, out var mods))
            {
                cmdView.SetAircraftSelectKeybind(key, mods);
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
