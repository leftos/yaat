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
        : this(preferences, facilityIdForGeometry: null, baseTitle: null) { }

    /// <summary>
    /// Popped-out strips window with optional per-facility geometry key and
    /// display title. Facility-scoped windows append the facility id to the
    /// geometry key so multiple popped-out windows each remember their own
    /// position. Caller owns the DataContext (the per-facility
    /// <c>VStripsViewModel</c>); this window only knows enough to size,
    /// position, and title itself.
    /// </summary>
    public VStripsViewWindow(UserPreferences preferences, string? facilityIdForGeometry, string? baseTitle)
    {
        InitializeComponent();

        var geometryKey = !string.IsNullOrEmpty(facilityIdForGeometry) ? $"VStripsView:{facilityIdForGeometry}" : "VStripsView";
        _geometryHelper = new WindowGeometryHelper(this, preferences, geometryKey, 1000, 600);
        _geometryHelper.Restore();

        if (!string.IsNullOrEmpty(baseTitle))
        {
            _geometryHelper.SetBaseTitle(baseTitle);
        }

        if (KeybindHelper.ParseKeybind(preferences.AlwaysOnTopKey, out var key, out var mods))
        {
            _alwaysOnTopKey = key;
            _alwaysOnTopModifiers = mods;
        }
    }

    /// <summary>
    /// Updates the popped-out window title when the underlying facility
    /// name changes (e.g. the entry switched facilities in-place). The host
    /// calls this whenever the tracked entry reports a title change.
    /// </summary>
    public void SetWindowTitle(string baseTitle)
    {
        _geometryHelper.SetBaseTitle(baseTitle);
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
