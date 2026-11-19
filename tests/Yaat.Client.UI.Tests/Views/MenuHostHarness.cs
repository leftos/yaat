using Avalonia;
using Avalonia.Controls;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Client.Views.Scope;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Builds the scope and ground aircraft menus the way the client hosts the views: each view in a shown
/// <see cref="Window"/> whose data context is the main view model, with the selection the right-click handler leaves —
/// the previously selected aircraft, else the right-clicked one.
/// </summary>
internal static class MenuHostHarness
{
    /// <summary>The primary scope's menu for <paramref name="ac"/>, with <paramref name="selected"/> as the previous selection.</summary>
    public static ContextMenu BuildScopeMenu(MainViewModel main, AircraftModel ac, AircraftModel? selected)
    {
        ScopeViewModel scope = main.PrimaryScope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window { DataContext = main, Content = view };
        window.Show();
        try
        {
            scope.SelectedAircraft = selected ?? ac;
            return view.BuildAircraftMenu(scope, ac, selected, ac.Callsign);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The primary scope's point menu for a right-click on empty map at <paramref name="position"/> with
    /// <paramref name="selected"/> selected.
    /// </summary>
    public static ContextMenu BuildScopePointMenu(MainViewModel main, AircraftModel selected, LatLon position)
    {
        ScopeViewModel scope = main.PrimaryScope;
        var view = new ScopeView { DataContext = scope };
        var window = new Window { DataContext = main, Content = view };
        window.Show();
        try
        {
            scope.SelectedAircraft = selected;
            return view.BuildMapPointMenu(scope, position, new Point(10, 10));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The ground menu for <paramref name="ac"/>, over the main view model's ground view model and its layout.</summary>
    public static ContextMenu BuildGroundMenu(MainViewModel main, AircraftModel ac, AircraftModel? selected)
    {
        var view = new GroundView { DataContext = main.Ground };
        var window = new Window { DataContext = main, Content = view };
        window.Show();
        try
        {
            main.Ground.SelectedAircraft = selected ?? ac;
            return view.BuildAircraftContextMenu(main.Ground, ac, selected, ac.Callsign);
        }
        finally
        {
            window.Close();
        }
    }
}
