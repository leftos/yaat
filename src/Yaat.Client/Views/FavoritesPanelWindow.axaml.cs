using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class FavoritesPanelWindow : Window
{
    private static readonly ConditionalWeakTable<MainViewModel, FavoritesPanelWindow> OpenWindows = new();

    private readonly WindowGeometryHelper _geometryHelper;
    private Key _alwaysOnTopKey = Key.None;
    private KeyModifiers _alwaysOnTopModifiers = KeyModifiers.None;

    public FavoritesPanelWindow()
        : this(new UserPreferences()) { }

    public FavoritesPanelWindow(UserPreferences preferences)
    {
        InitializeComponent();
        _geometryHelper = new WindowGeometryHelper(this, preferences, "FavoritesPanel", 900, 620);
        _geometryHelper.Restore();
        AlwaysOnTopContextMenu.Attach(this, _geometryHelper);
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

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainViewModel vm && SettingsViewModel.ParseKeybind(vm.Preferences.AlwaysOnTopKey, out var key, out var mods))
        {
            _alwaysOnTopKey = key;
            _alwaysOnTopModifiers = mods;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == _alwaysOnTopKey && e.KeyModifiers == _alwaysOnTopModifiers)
        {
            _geometryHelper.ToggleTopmost();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
