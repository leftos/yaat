using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Scope's right-click menus end with "Settings for this view…", which asks the main view model for Settings opened at
/// the radar section the Scope shares.
/// </summary>
public class ScopeViewSettingsMenuTests
{
    private const string Header = "Settings for this view…";

    [AvaloniaFact]
    public async Task MapMenu_EndsWithSettingsForThisView_OpeningRadar()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = await ShowScopeAsync();
        try
        {
            ContextMenu menu = view.BuildMapContextMenu(scope, new LatLon(37.620, -122.380), new Point(10, 10));

            AssertLastItemRequests(menu, main);
        }
        finally
        {
            Close(window, view);
        }
    }

    private static void AssertLastItemRequests(ContextMenu menu, MainViewModel mainVm)
    {
        MenuItem last = Assert.IsType<MenuItem>(menu.Items[^1]);
        Assert.Equal(Header, last.Header);
        // A separator sets it apart from the point section's items.
        Assert.IsType<Separator>(menu.Items[^2]);

        List<SettingsSectionId?> requests = [];
        mainVm.SettingsRequested += requests.Add;
        last.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([SettingsSectionId.Radar], requests);
    }

    private static async Task<(MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window)> ShowScopeAsync()
    {
        (MainViewModel main, ScopeViewModel scope) = await ScopeTestFixture.OpenScopeAsync();
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return (main, scope, view, window);
    }

    private static void Close(Window window, ScopeView view)
    {
        view.ActiveContextMenu?.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
