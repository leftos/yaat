using Avalonia.Controls;
using Yaat.Client.Services;

namespace Yaat.Client.Views.VStrips;

public partial class VStripsViewWindow : Window, IAlwaysOnTopToggle
{
    private readonly WindowGeometryHelper _geometryHelper;

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
        var hasSavedGeometry = preferences.GetWindowGeometry(geometryKey) is not null;
        _geometryHelper = new WindowGeometryHelper(this, preferences, geometryKey, 1000, 600);
        _geometryHelper.Restore();

        // First-time per-facility window: inherit Topmost from the global "VStripsView" default
        // so the Settings checkbox affects newly opened facility-scoped windows too.
        if (!hasSavedGeometry && !string.IsNullOrEmpty(facilityIdForGeometry))
        {
            var globalGeometry = preferences.GetWindowGeometry("VStripsView");
            if (globalGeometry?.IsTopmost == true)
            {
                Topmost = true;
            }
        }

        if (!string.IsNullOrEmpty(baseTitle))
        {
            _geometryHelper.SetBaseTitle(baseTitle);
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

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
