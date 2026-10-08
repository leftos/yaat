using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The right-click menus of the radar, ground, aircraft list and terminal views end with "Settings for this view…", which asks
/// the main view model for Settings opened at that view's section.
/// </summary>
public class ViewSettingsMenuTests
{
    private const string Header = "Settings for this view…";
    private const double Lat = 37.620;
    private const double Lon = -122.380;

    [AvaloniaFact]
    public void RadarMapMenu_EndsWithSettingsForThisView_OpeningRadar()
    {
        var mainVm = new MainViewModel(new FakeFilePickerService());
        var view = new RadarView { DataContext = mainVm.Radar };
        var host = new Grid { DataContext = mainVm };
        host.Children.Add(view);

        ContextMenu? menu = view.BuildMapContextMenu(Lat, Lon, new Point(10, 10));

        AssertLastItemRequests(menu, mainVm, SettingsSectionId.Radar);
    }

    [AvaloniaFact]
    public void GroundMenu_EndsWithSettingsForThisView_OpeningGround()
    {
        var mainVm = new MainViewModel(new FakeFilePickerService());
        var groundVm = new GroundViewModel(new ServerConnection(), sendCommand: (_, _, _) => Task.CompletedTask)
        {
            RoomActiveRunways = () => new Dictionary<string, IReadOnlyList<string>>(),
        };
        groundVm.SetLayoutForTesting(
            new GroundLayoutDto("TST", [new GroundNodeDto(1, Lat, Lon, "TaxiwayIntersection", null, null, null)], [], null, null, null)
        );
        var view = new GroundView { DataContext = groundVm };
        var host = new Grid { DataContext = mainVm };
        host.Children.Add(view);

        ContextMenu? menu = view.BuildNodeContextMenu(1, new Point(10, 10));

        AssertLastItemRequests(menu, mainVm, SettingsSectionId.Ground);
    }

    [AvaloniaTheory]
    [InlineData(null)]
    [InlineData("Delayed (30s)")]
    public void AircraftListMenu_EndsWithSettingsForThisView_OpeningAircraftList(string? status)
    {
        var mainVm = new MainViewModel(new FakeFilePickerService());
        var ac = new AircraftModel
        {
            Callsign = "AAL123",
            AircraftType = "B738",
            FlightRules = "IFR",
            Position = new LatLon(Lat, Lon),
        };
        if (status is not null)
        {
            ac.Status = status;
        }

        ContextMenu menu = DataGridView.BuildRowContextMenu(mainVm, new DataGrid(), ac, null, [ac]);

        AssertLastItemRequests(menu, mainVm, SettingsSectionId.AircraftList);
    }

    [AvaloniaFact]
    public void RadarAircraftMenu_EndsWithSettingsForThisView_OpeningRadar()
    {
        var mainVm = new MainViewModel(new FakeFilePickerService());
        AircraftModel ac = Aircraft();
        var view = new RadarView { DataContext = mainVm.Radar };
        var host = new Grid { DataContext = mainVm };
        host.Children.Add(view);

        ContextMenu menu = view.BuildAircraftRightClickMenu(mainVm.Radar, ac, null, ac.Callsign);

        AssertLastItemRequests(menu, mainVm, SettingsSectionId.Radar);
    }

    [AvaloniaFact]
    public void GroundAircraftMenu_EndsWithSettingsForThisView_OpeningGround()
    {
        var mainVm = new MainViewModel(new FakeFilePickerService());
        AircraftModel ac = Aircraft();
        var view = new GroundView { DataContext = mainVm.Ground };
        var host = new Grid { DataContext = mainVm };
        host.Children.Add(view);

        ContextMenu menu = view.BuildAircraftRightClickMenu(mainVm.Ground, ac, null, ac.Callsign);

        AssertLastItemRequests(menu, mainVm, SettingsSectionId.Ground);
    }

    [AvaloniaFact]
    public void TerminalMenu_EndsWithSettingsForThisView_OpeningTerminal()
    {
        var mainVm = new MainViewModel(new FakeFilePickerService());
        var view = new TerminalPanelView { DataContext = mainVm };
        var window = new Window
        {
            Width = 600,
            Height = 300,
            Content = view,
        };
        window.ShowAndRunLayout();

        try
        {
            AssertLastItemRequests(view.FindControl<Control>("TerminalEditor")!.ContextMenu, mainVm, SettingsSectionId.Terminal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RadarMapMenu_WithoutAMainViewModelAncestor_HasNoSettingsForThisViewItem()
    {
        var mainVm = new MainViewModel(new FakeFilePickerService());
        var view = new RadarView { DataContext = mainVm.Radar };
        // No host carrying the main view model: the view cannot reach one, so it must not offer the item.
        var host = new Grid();
        host.Children.Add(view);

        ContextMenu? menu = view.BuildMapContextMenu(Lat, Lon, new Point(10, 10));

        Assert.NotNull(menu);
        Assert.DoesNotContain(menu.Items, i => ((i as MenuItem)?.Header as string) == Header);
    }

    private static AircraftModel Aircraft() =>
        new()
        {
            Callsign = "AAL123",
            AircraftType = "B738",
            FlightRules = "IFR",
            Position = new LatLon(Lat, Lon),
        };

    private static void AssertLastItemRequests(ContextMenu? menu, MainViewModel mainVm, SettingsSectionId section)
    {
        Assert.NotNull(menu);
        MenuItem last = Assert.IsType<MenuItem>(menu!.Items[^1]);
        Assert.Equal(Header, last.Header);
        // A separator sets it apart from the view's own items; a menu with none (ground, no measuring tool) has it alone.
        if (menu.Items.Count > 1)
        {
            Assert.IsType<Separator>(menu.Items[^2]);
        }

        List<SettingsSectionId?> requests = [];
        mainVm.SettingsRequested += requests.Add;
        last.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([section], requests);
    }
}
