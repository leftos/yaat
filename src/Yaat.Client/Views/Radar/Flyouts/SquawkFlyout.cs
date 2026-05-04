using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Transponder/squawk quick-action picker. VFR / Standby / Normal / Ident / Random.
/// For specific 4-digit codes use the typed command bar (SQ 1234).
/// </summary>
internal static class SquawkFlyout
{
    public static ContextMenu Build(AircraftModel aircraft, RadarViewModel radarVm, string initials)
    {
        var menu = new ContextMenu();
        FlyoutAppearance.ApplyFontSize(menu);
        menu.Items.Add(
            new MenuItem
            {
                Header = $"Squawk — {aircraft.Callsign}",
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        menu.Items.Add(new Separator());

        Add(menu, "Squawk Normal (SN)", radarVm, aircraft, initials, "SN");
        Add(menu, "Squawk Standby (SS)", radarVm, aircraft, initials, "SS");
        Add(menu, "Squawk VFR (SQVFR)", radarVm, aircraft, initials, "SQVFR");
        Add(menu, "Ident (ID)", radarVm, aircraft, initials, "ID");
        Add(menu, "Random Squawk (RANDSQ)", radarVm, aircraft, initials, "RANDSQ");

        menu.Items.Add(new Separator());
        menu.Items.Add(
            new MenuItem
            {
                Header = "(For specific code: type 'SQ 1234' in the command bar)",
                IsEnabled = false,
                FontStyle = FontStyle.Italic,
            }
        );
        return menu;
    }

    private static void Add(ContextMenu menu, string header, RadarViewModel vm, AircraftModel ac, string initials, string command)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await vm.SendRawCommandAsync(ac.Callsign, initials, command);
        menu.Items.Add(item);
    }
}
