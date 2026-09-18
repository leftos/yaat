using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class FavoritesPanelWindow : Window, IAlwaysOnTopToggle
{
    private static readonly ConditionalWeakTable<MainViewModel, FavoritesPanelWindow> OpenWindows = [];

    private readonly WindowGeometryHelper _geometryHelper;

    public FavoritesPanelWindow()
        : this(new UserPreferences()) { }

    public FavoritesPanelWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "FavoritesPanel", 900, 620);
        _geometryHelper.Restore();
    }

    /// <summary>True while a panel window is open for this view model (window profiles capture this).</summary>
    public static bool IsOpen(MainViewModel vm) => OpenWindows.TryGetValue(vm, out _);

    /// <summary>Closes the panel window for this view model, if one is open.</summary>
    public static void Close(MainViewModel vm)
    {
        if (OpenWindows.TryGetValue(vm, out FavoritesPanelWindow? existing))
        {
            existing.Close();
        }
    }

    public static FavoritesPanelWindow ShowOrActivate(MainViewModel vm)
    {
        if (OpenWindows.TryGetValue(vm, out FavoritesPanelWindow? existing))
        {
            existing.RestoreAndActivate();
            return existing;
        }

        var window = new FavoritesPanelWindow(vm.Preferences) { DataContext = vm };
        OpenWindows.Add(vm, window);
        window.Closed += (_, _) =>
        {
            OpenWindows.Remove(vm);

            // Same rule as every pop-out's Closing handler: only a user-initiated close clears the
            // persisted "open" flag. During app shutdown the framework closes this window, and the
            // flag must survive so the next launch reopens the panel.
            if (!AppLifetime.IsShuttingDown)
            {
                vm.Preferences.SetFavoritesPanelOpen(false);
            }
        };
        vm.Preferences.SetFavoritesPanelOpen(true);

        // Shown un-owned (bare Show()) like every other persistent tool window (Controllers, Metar,
        // popped-out Radar/Ground/DataGrid). An owned window is forced above its owner in Z-order and
        // can't be sent behind, which made the panel feel like it blocked the main window (#287).
        window.Show();
        return window;
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
