using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class ControllersWindow : Window
{
    private readonly WindowGeometryHelper _geometryHelper;
    private Key _alwaysOnTopKey = Key.None;
    private KeyModifiers _alwaysOnTopModifiers = KeyModifiers.None;

    public ControllersWindow()
        : this(new UserPreferences()) { }

    public ControllersWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "Controllers", 360, 480);
        _geometryHelper.Restore();
        AlwaysOnTopContextMenu.Attach(this, _geometryHelper);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm && SettingsViewModel.ParseKeybind(vm.Preferences.AlwaysOnTopKey, out var key, out var mods))
        {
            _alwaysOnTopKey = key;
            _alwaysOnTopModifiers = mods;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == _alwaysOnTopKey && e.KeyModifiers == _alwaysOnTopModifiers)
        {
            _geometryHelper.ToggleTopmost();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
