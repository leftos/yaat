using Avalonia.Controls;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class MetarWindow : Window, IAlwaysOnTopToggle
{
    private readonly WindowGeometryHelper _geometryHelper;

    public MetarWindow()
        : this(new UserPreferences()) { }

    public MetarWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "Metar", 520, 480);
        _geometryHelper.Restore();
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
