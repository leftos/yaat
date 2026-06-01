using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class MetarWindow : Window
{
    private static readonly ConditionalWeakTable<MainViewModel, MetarWindow> OpenWindows = new();

    public MetarWindow()
        : this(new UserPreferences()) { }

    public MetarWindow(UserPreferences preferences)
    {
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "Metar", 520, 480).Restore();
    }

    public static void ShowOrActivate(MainViewModel vm, Window? owner)
    {
        if (OpenWindows.TryGetValue(vm, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new MetarWindow(vm.Preferences) { DataContext = vm };
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
}
