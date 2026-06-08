using Avalonia.Controls;
using Yaat.Client.Services;

namespace Yaat.Client.Views.Ground;

public partial class GroundViewWindow : Window, IAlwaysOnTopToggle
{
    private readonly WindowGeometryHelper _geometryHelper;

    public GroundViewWindow()
        : this(new UserPreferences()) { }

    public GroundViewWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "GroundView", 800, 600);
        _geometryHelper.Restore();
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
