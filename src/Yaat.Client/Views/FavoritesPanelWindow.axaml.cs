using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class FavoritesPanelWindow : Window, IAlwaysOnTopToggle
{
    private static readonly ConditionalWeakTable<MainViewModel, FavoritesPanelWindow> OpenWindows = new();

    private readonly WindowGeometryHelper _geometryHelper;

    public FavoritesPanelWindow()
        : this(new UserPreferences()) { }

    public FavoritesPanelWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "FavoritesPanel", 900, 620);
        _geometryHelper.Restore();
    }

    public static void ShowOrActivate(MainViewModel vm)
    {
        if (OpenWindows.TryGetValue(vm, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new FavoritesPanelWindow(vm.Preferences) { DataContext = vm };
        OpenWindows.Add(vm, window);
        window.Closed += (_, _) => OpenWindows.Remove(vm);

        // Shown un-owned (bare Show()) like every other persistent tool window (Controllers, Metar,
        // popped-out Radar/Ground/DataGrid). An owned window is forced above its owner in Z-order and
        // can't be sent behind, which made the panel feel like it blocked the main window (#287).
        window.Show();
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
