using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// The EuroScope tag's assigned-speed picker, opened by a click on the speed fields: Resume Normal Speed, 350 down to 80 kt in
/// tens, then the Reduce to final approach speed entry; not the quick-command speed flyout.
/// </summary>
internal static class RadarSpeedFlyout
{
    private const int MinSpeed = 80;
    private const int MaxSpeed = 350;
    private const int Step = 10;

    public static ContextMenu Build(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var menu = new ContextMenu();
        FlyoutAppearance.ApplyFontSize(menu);
        RadarAltitudeFlyout.ApplyScrollCap(menu);

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

        RadarAltitudeFlyout.ScrollFocusItemIntoView(menu, focusItem);

        return menu;
    }

    private static int RoundToStep(int value, int step) => (int)Math.Round((double)value / step) * step;

    internal static MenuItem BuildFasItem(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        string header = MenuCatalog.FinalApproachSpeedLabel(MenuCatalog.Get(MenuIds.SpeedFinalApproach).Label, aircraft);

        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await radarVm.ReduceFinalApproachSpeedAsync(aircraft.Callsign, initials);
        return item;
    }
}
