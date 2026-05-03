using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Assigned runway picker. Currently shows the assigned runway if set plus a hint to use
/// the typed command bar — listing live runway candidates from the destination airport
/// requires NavData lookup which a follow-up will plumb through.
/// </summary>
internal static class RunwayFlyout
{
    public static ContextMenu Build(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var menu = new ContextMenu();
        menu.Items.Add(
            new MenuItem
            {
                Header = $"Runway — {aircraft.Callsign}",
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        if (!string.IsNullOrEmpty(aircraft.AssignedRunway))
        {
            menu.Items.Add(
                new MenuItem
                {
                    Header = $"Currently assigned: {aircraft.AssignedRunway}",
                    IsEnabled = false,
                    FontStyle = FontStyle.Italic,
                }
            );
        }
        menu.Items.Add(new Separator());

        var clear = new MenuItem { Header = "Clear assigned runway" };
        clear.Click += async (_, _) => await radarVm.SendRawCommandAsync(aircraft.Callsign, initials, "RWY");
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        menu.Items.Add(
            new MenuItem
            {
                Header = "(To assign: type 'RWY 28L' in the command bar)",
                IsEnabled = false,
                FontStyle = FontStyle.Italic,
            }
        );
        return menu;
    }
}
