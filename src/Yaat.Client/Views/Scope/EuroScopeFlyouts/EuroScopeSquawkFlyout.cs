using Avalonia.Controls;
using Avalonia.Media;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope.EuroScopeFlyouts;

/// <summary>
/// Transponder/squawk quick-action picker. VFR / Standby / Normal / Ident / Random.
/// For specific 4-digit codes use the typed command bar (SQ 1234).
/// </summary>
internal static class EuroScopeSquawkFlyout
{
    public static ContextMenu Build(AircraftModel aircraft, IMenuHost host)
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

        Add(menu, "Squawk Normal (SN)", host, aircraft, "SN");
        Add(menu, "Squawk Standby (SS)", host, aircraft, "SS");
        Add(menu, "Squawk VFR (SQVFR)", host, aircraft, "SQVFR");
        Add(menu, "Ident (IDENT)", host, aircraft, "IDENT");
        Add(menu, "Random Squawk (RANDSQ)", host, aircraft, "RANDSQ");

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

    private static void Add(ContextMenu menu, string header, IMenuHost host, AircraftModel ac, string command)
    {
        string initials = host.Session.Initials;
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await host.SendAsync(ac.Callsign, command, initials);
        menu.Items.Add(item);
    }
}
