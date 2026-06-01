using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

public partial class ControllersWindow : Window
{
    private static readonly ConditionalWeakTable<MainViewModel, ControllersWindow> OpenWindows = new();

    public ControllersWindow()
        : this(new UserPreferences()) { }

    public ControllersWindow(UserPreferences preferences)
    {
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "Controllers", 640, 420).Restore();
    }

    public static void ShowOrActivate(MainViewModel vm, Window? owner)
    {
        if (OpenWindows.TryGetValue(vm, out var existing))
        {
            existing.Activate();
            _ = vm.RefreshOnlineControllersCommand.ExecuteAsync(null);
            return;
        }

        var window = new ControllersWindow(vm.Preferences) { DataContext = vm };
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

        _ = vm.RefreshOnlineControllersCommand.ExecuteAsync(null);
    }
}
