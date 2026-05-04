using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// EuroScope-style assigned-speed picker. ContextMenu of 80..350 kt in 10-kt steps plus
/// "Resume Normal Speed" and "Final Approach Speed" entries. Selection routes through
/// RadarViewModel.SpeedAsync / SpeedNormalAsync / ReduceFinalApproachSpeedAsync.
/// </summary>
internal static class SpeedFlyout
{
    private const int MinSpeed = 80;
    private const int MaxSpeed = 350;
    private const int Step = 10;

    public static ContextMenu Build(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var menu = new ContextMenu();
        FlyoutAppearance.ApplyFontSize(menu);
        AltitudeFlyout.ApplyScrollCap(menu);

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
        int currentSpeed = (int)Math.Round(aircraft.GroundSpeed);
        int focusSpeed = assignedSpeed ?? RoundToStep(Math.Clamp(currentSpeed, MinSpeed, MaxSpeed), Step);

        MenuItem? focusItem = null;

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

            if (spd == focusSpeed)
            {
                focusItem = item;
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildFasItem(aircraft, radarVm, initials));

        AltitudeFlyout.ScrollFocusItemIntoView(menu, focusItem);

        return menu;
    }

    private static int RoundToStep(int value, int step) => (int)Math.Round((double)value / step) * step;

    internal static MenuItem BuildFasItem(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var category = AircraftCategorization.Categorize(aircraft.FiledAircraftType);
        var fas = AircraftPerformance.ApproachSpeed(aircraft.FiledAircraftType, category);
        var header = fas > 0 ? $"FAS - {fas:F0} kt" : "FAS";

        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await radarVm.ReduceFinalApproachSpeedAsync(aircraft.Callsign, initials);
        return item;
    }
}
