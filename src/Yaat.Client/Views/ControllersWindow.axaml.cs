using Avalonia.Controls;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class ControllersWindow : Window, IAlwaysOnTopToggle
{
    private readonly WindowGeometryHelper _geometryHelper;

    public ControllersWindow()
        : this(new UserPreferences()) { }

    public ControllersWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "Controllers", 360, 480);
        _geometryHelper.Restore();
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
