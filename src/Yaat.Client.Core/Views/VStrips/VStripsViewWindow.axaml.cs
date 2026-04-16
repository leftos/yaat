using Avalonia.Controls;
using Avalonia.Input;
using Yaat.Client.Services;

namespace Yaat.Client.Views.VStrips;

public partial class VStripsViewWindow : Window
{
    private readonly WindowGeometryHelper _geometryHelper;
    private Key _alwaysOnTopKey = Key.None;
    private KeyModifiers _alwaysOnTopModifiers = KeyModifiers.None;

    public VStripsViewWindow()
        : this(new UserPreferences()) { }

    public VStripsViewWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "VStripsView", 1000, 600);
        _geometryHelper.Restore();

        if (KeybindHelper.ParseKeybind(preferences.AlwaysOnTopKey, out var key, out var mods))
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
