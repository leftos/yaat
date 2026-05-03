using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// EuroScope-style assigned-speed picker. ContextMenu of 80..350 kt in 10-kt steps plus
/// a "Resume Normal Speed" entry. Selection routes through RadarViewModel.SpeedAsync /
/// SpeedNormalAsync.
/// </summary>
internal static class SpeedFlyout
{
    private const int MinSpeed = 80;
    private const int MaxSpeed = 350;
    private const int Step = 10;

    public static ContextMenu Build(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var menu = new ContextMenu();

        menu.Items.Add(
            new MenuItem
            {
                Header = $"Speed — {aircraft.Callsign}",
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        menu.Items.Add(new Separator());

        var resumeItem = new MenuItem { Header = "Resume Normal Speed (RNS)" };
        resumeItem.Click += async (_, _) => await radarVm.SpeedNormalAsync(aircraft.Callsign, initials);
        menu.Items.Add(resumeItem);
        menu.Items.Add(new Separator());

        int? assignedSpeed = aircraft.AssignedSpeed is double s ? (int)s : null;

        for (int spd = MaxSpeed; spd >= MinSpeed; spd -= Step)
        {
            int target = spd;
            string label = $"{spd:D3} kt";
            if (assignedSpeed == target)
            {
                label = $"▶ {label}";
            }
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await radarVm.SpeedAsync(aircraft.Callsign, initials, target);
            menu.Items.Add(item);
        }

        return menu;
    }
}
