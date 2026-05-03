using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views.Radar.Flyouts;

/// <summary>
/// Scratchpad picker. Offers a clear action plus EuroScope-convention preset values
/// (CLEA / NOTC / ST-UP / PUSH / TAXI / DEPA — see docs/euroscope/flight-data.md).
/// Custom strings should be entered via the typed command bar (SP1 ABC / SP2 ABC).
/// </summary>
internal static class ScratchpadFlyout
{
    public static ContextMenu Build(AircraftModel aircraft, RadarViewModel radarVm, string initials, int slot)
    {
        var verb = slot == 2 ? "SP2" : "SP1";
        var current = slot == 2 ? aircraft.Scratchpad2 : aircraft.Scratchpad1;

        var menu = new ContextMenu();
        menu.Items.Add(
            new MenuItem
            {
                Header = $"Scratchpad {slot} — {aircraft.Callsign}",
                IsEnabled = false,
                FontWeight = FontWeight.Bold,
            }
        );
        if (!string.IsNullOrEmpty(current))
        {
            menu.Items.Add(
                new MenuItem
                {
                    Header = $"Current: \"{current}\"",
                    IsEnabled = false,
                    FontStyle = FontStyle.Italic,
                }
            );
        }
        menu.Items.Add(new Separator());

        var clear = new MenuItem { Header = "Clear" };
        clear.Click += async (_, _) => await radarVm.SendRawCommandAsync(aircraft.Callsign, initials, verb);
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());

        foreach (var preset in new[] { "CLEA", "NOTC", "ST-UP", "PUSH", "TAXI", "DEPA" })
        {
            var p = preset;
            var item = new MenuItem { Header = $"Set: {p}" };
            item.Click += async (_, _) => await radarVm.SendRawCommandAsync(aircraft.Callsign, initials, $"{verb} {p}");
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(
            new MenuItem
            {
                Header = $"(For custom strings: type '{verb} ABC' in the command bar)",
                IsEnabled = false,
                FontStyle = FontStyle.Italic,
            }
        );
        return menu;
    }
}
