using Avalonia.Controls;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Radar;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Builds the radar and ground aircraft menus the way the client hosts the views: each view in a shown
/// <see cref="Window"/> whose data context is the main view model, with the selection the right-click handler leaves —
/// the previously selected aircraft, else the right-clicked one.
/// </summary>
internal static class MenuHostHarness
{
    /// <summary>The radar menu for <paramref name="ac"/>, with <paramref name="selected"/> as the previous selection.</summary>
    public static ContextMenu BuildRadarMenu(MainViewModel main, AircraftModel ac, AircraftModel? selected)
    {
        var view = new RadarView { DataContext = main.Radar };
        var window = new Window { DataContext = main, Content = view };
        window.Show();
        try
        {
            main.Radar.SelectedAircraft = selected ?? ac;
            return view.BuildAircraftContextMenu(main.Radar, ac, selected, ac.Callsign);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The ground menu for <paramref name="ac"/>, over the main view model's ground view model and its layout.</summary>
    public static ContextMenu BuildGroundMenu(MainViewModel main, AircraftModel ac, AircraftModel? selected, string initials)
    {
        var view = new GroundView { DataContext = main.Ground };
        var window = new Window { DataContext = main, Content = view };
        window.Show();
        try
        {
            main.Ground.SelectedAircraft = selected ?? ac;
            return view.BuildAircraftContextMenu(main.Ground, new GroundMenuTarget(ac, selected, ac.Callsign, initials));
        }
        finally
        {
            window.Close();
        }
    }
}
