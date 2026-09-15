using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar;

public partial class RadarViewWindow : Window, IAlwaysOnTopToggle
{
    private readonly WindowGeometryHelper _geometryHelper;

    public RadarViewWindow()
        : this(new UserPreferences(), "RadarView", "Radar View") { }

    /// <summary>
    /// Hosts one Radar View. The popped-out primary passes the fixed <c>"RadarView"</c> key; an extra
    /// instance passes its own <c>RadarView#n</c> key and numbered title, so each window keeps its own
    /// geometry and topmost preference.
    /// </summary>
    /// <param name="preferences">The application preferences backing geometry and topmost state.</param>
    /// <param name="geometryKey">Window-geometry key, e.g. <c>"RadarView"</c> or <c>"RadarView#2"</c>.</param>
    /// <param name="title">Window title, e.g. <c>"Radar View"</c> or <c>"Radar View #2"</c>.</param>
    public RadarViewWindow(UserPreferences preferences, string geometryKey, string title)
    {
        InitializeComponent();
        // Set before Restore(): the geometry helper captures the current title as the base it appends
        // the pinned marker to, so a title assigned afterwards would lose (or duplicate) that marker.
        Title = title;
        _geometryHelper = new WindowGeometryHelper(this, preferences, geometryKey, 800, 600);
        _geometryHelper.Restore();
    }

    /// <summary>
    /// The view-model driving this window's Radar View, or null before <see cref="SetViewModel"/> ran.
    /// The window's own DataContext stays the <see cref="MainViewModel"/>.
    /// </summary>
    public RadarViewModel? RadarVm { get; private set; }

    /// <summary>Points the hosted <see cref="RadarView"/> at <paramref name="vm"/>.</summary>
    public void SetViewModel(RadarViewModel vm)
    {
        RadarVm = vm;
        Host.DataContext = vm;
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
