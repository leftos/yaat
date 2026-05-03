using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// EuroScope-style assigned-altitude picker. Builds a ContextMenu with FL010..FL300 in
/// 1000-ft steps; selection dispatches CM (climb) or DM (descend) based on current
/// altitude. The menu pops at the pointer; outside-click auto-closes via Avalonia's
/// ContextMenu light-dismiss behaviour.
/// </summary>
internal static class AltitudeFlyout
{
    private const int MinFlHundreds = 10;
    private const int MaxFlHundreds = 300;
    private const int StepHundreds = 10;

    public static ContextMenu Build(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var menu = new ContextMenu();

        menu.Items.Add(
            new MenuItem
            {
                Header = $"Altitude — {aircraft.Callsign}",
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        menu.Items.Add(new Separator());

        int currentFlHundreds = (int)aircraft.Altitude / 100;
        int? assignedFlHundreds = aircraft.AssignedAltitude is double a ? (int)a / 100 : null;

        for (int fl = MaxFlHundreds; fl >= MinFlHundreds; fl -= StepHundreds)
        {
            int targetFl = fl;
            string label = $"FL{fl:D3}";
            if (assignedFlHundreds == fl)
            {
                // Marker for the currently assigned altitude.
                label = $"▶ {label}";
            }

            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await DispatchAsync(aircraft, radarVm, initials, targetFl, currentFlHundreds);
            menu.Items.Add(item);
        }

        return menu;
    }

    private static async Task DispatchAsync(AircraftModel ac, RadarViewModel vm, string initials, int targetFlHundreds, int currentFlHundreds)
    {
        if (targetFlHundreds >= currentFlHundreds)
        {
            await vm.ClimbAndMaintainAsync(ac.Callsign, initials, targetFlHundreds);
        }
        else
        {
            await vm.DescendAndMaintainAsync(ac.Callsign, initials, targetFlHundreds);
        }
    }
}
