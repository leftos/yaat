using Avalonia.Controls;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class DataGridWindow : Window, IAlwaysOnTopToggle
{
    private readonly WindowGeometryHelper _geometryHelper;

    public DataGridWindow()
        : this(new UserPreferences()) { }

    public DataGridWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "DataGrid", 1000, 600);
        _geometryHelper.Restore();
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
