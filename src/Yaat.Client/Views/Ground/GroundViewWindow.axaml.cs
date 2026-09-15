using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Ground;

public partial class GroundViewWindow : Window, IAlwaysOnTopToggle
{
    private readonly WindowGeometryHelper _geometryHelper;

    public GroundViewWindow()
        : this(new UserPreferences(), "GroundView", "Ground View") { }

    /// <summary>
    /// Hosts one Ground View. The popped-out primary passes the fixed <c>"GroundView"</c> key; an extra
    /// instance passes its own <c>GroundView#n</c> key and numbered title, so each window keeps its own
    /// geometry and topmost preference.
    /// </summary>
    /// <param name="preferences">The application preferences backing geometry and topmost state.</param>
    /// <param name="geometryKey">Window-geometry key, e.g. <c>"GroundView"</c> or <c>"GroundView#2"</c>.</param>
    /// <param name="title">Window title, e.g. <c>"Ground View"</c> or <c>"Ground View #2"</c>.</param>
    public GroundViewWindow(UserPreferences preferences, string geometryKey, string title)
    {
        InitializeComponent();
        // Set before Restore(): the geometry helper captures the current title as the base it appends
        // the pinned marker to, so a title assigned afterwards would lose (or duplicate) that marker.
        Title = title;
        _geometryHelper = new WindowGeometryHelper(this, preferences, geometryKey, 800, 600);
        _geometryHelper.Restore();
    }

    /// <summary>
    /// The view-model driving this window's Ground View, or null before <see cref="SetViewModel"/> ran.
    /// The window's own DataContext stays the <see cref="MainViewModel"/>.
    /// </summary>
    public GroundViewModel? GroundVm { get; private set; }

    /// <summary>Points the hosted <see cref="GroundView"/> at <paramref name="vm"/>.</summary>
    public void SetViewModel(GroundViewModel vm)
    {
        GroundVm = vm;
        Host.DataContext = vm;
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
