using Avalonia.Controls;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Radar;

public partial class RadarViewWindow : Window, IAlwaysOnTopToggle
{
    private readonly WindowGeometryHelper _geometryHelper;

    public RadarViewWindow()
        : this(new UserPreferences()) { }

    public RadarViewWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "RadarView", 800, 600);
        _geometryHelper.Restore();
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
