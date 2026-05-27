using Avalonia.Controls;
using Avalonia.Input;
using Yaat.Client.Services;

namespace Yaat.Client.Views.VTdls;

/// <summary>
/// Popped-out vTDLS window. One instance per active facility (the in-tab view
/// uses the embedded <see cref="VTdlsView"/> UserControl directly — no Window
/// wrapper). Geometry key carries the facility id so each pop-out remembers
/// its own size/position across restarts.
/// </summary>
public partial class VTdlsViewWindow : Window
{
    private readonly WindowGeometryHelper _geometryHelper;
    private Key _alwaysOnTopKey = Key.None;
    private KeyModifiers _alwaysOnTopModifiers = KeyModifiers.None;

    public VTdlsViewWindow()
        : this(new UserPreferences()) { }

    public VTdlsViewWindow(UserPreferences preferences)
        : this(preferences, facilityIdForGeometry: null, baseTitle: null) { }

    /// <summary>
    /// Popped-out vTDLS window with optional per-facility geometry key and
    /// display title. Facility-scoped windows append the facility id to the
    /// geometry key so multiple popped-out windows each remember their own
    /// position. Caller owns the DataContext (the per-facility
    /// <c>VTdlsViewModel</c>); this window only knows enough to size, position,
    /// and title itself.
    /// </summary>
    public VTdlsViewWindow(UserPreferences preferences, string? facilityIdForGeometry, string? baseTitle)
    {
        InitializeComponent();

        var geometryKey = !string.IsNullOrEmpty(facilityIdForGeometry) ? $"VTdlsView:{facilityIdForGeometry}" : "VTdlsView";
        var hasSavedGeometry = preferences.GetWindowGeometry(geometryKey) is not null;
        _geometryHelper = new WindowGeometryHelper(this, preferences, geometryKey, 900, 600);
        _geometryHelper.Restore();

        // First-time per-facility window: inherit Topmost from the global
        // "VTdlsView" default so the Settings checkbox affects newly opened
        // facility-scoped windows too. Mirrors the Strips behavior.
        if (!hasSavedGeometry && !string.IsNullOrEmpty(facilityIdForGeometry))
        {
            var globalGeometry = preferences.GetWindowGeometry("VTdlsView");
            if (globalGeometry?.IsTopmost == true)
            {
                Topmost = true;
            }
        }

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

    /// <summary>Updates the popped-out window title when the facility name changes (e.g. the entry switched facilities in place).</summary>
    public void SetWindowTitle(string baseTitle) => _geometryHelper.SetBaseTitle(baseTitle);

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
