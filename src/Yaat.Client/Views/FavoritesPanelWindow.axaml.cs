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

    public static void ShowOrActivate(MainViewModel vm, Window? owner)
    {
        if (OpenWindows.TryGetValue(vm, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new FavoritesPanelWindow(vm.Preferences) { DataContext = vm };
        OpenWindows.Add(vm, window);
        window.Closed += (_, _) => OpenWindows.Remove(vm);

        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    public void ToggleAlwaysOnTop() => _geometryHelper.ToggleTopmost();
}
