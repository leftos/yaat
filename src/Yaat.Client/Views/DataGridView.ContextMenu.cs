using Avalonia.Controls;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Views;

/// <summary>
/// Phase-aware context menu builders for the aircraft list. Mirrors the
/// content of Ground/Radar context menus so an RPO can act on an aircraft
/// without first finding it on a scope. Items needing free-text input or
/// filtered list popups (e.g. Direct to fix...) are omitted — those remain
/// reachable via the inline command box at the top of the menu.
/// </summary>
public partial class DataGridView
{
    private static MenuItem MakeItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static void AddPhaseAwareItems(ContextMenu menu, AircraftModel ac, MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);

        var phase = ac.CurrentPhase ?? "";
        var rwy = !string.IsNullOrEmpty(ac.AssignedRunway) ? $" {ac.AssignedRunway}" : "";

        if (ac.IsOnGround)
        {
            if (phase == "At Parking")
            {
                menu.Items.Add(MakeItem("Push back", () => Cmd("PUSH")));
            }

            if (phase is "Pushback" or "Pushback to Spot" or "Taxiing" || phase.StartsWith("Following", StringComparison.Ordinal))
            {
                menu.Items.Add(MakeItem("Hold position", () => Cmd("HOLD")));
            }

            if (phase.StartsWith("Holding Short", StringComparison.Ordinal))
            {
                menu.Items.Add(MakeItem("Resume taxi", () => Cmd("RES")));
                if (!string.IsNullOrEmpty(ac.AssignedRunway))
                {
                    var r = ac.AssignedRunway;
                    menu.Items.Add(MakeItem($"Cross {r}", () => Cmd($"CROSS {r}")));
                    menu.Items.Add(MakeItem($"Line up and wait {r}", () => Cmd($"LUAW {r}")));
                }
            }

            if (phase is "Holding After Exit" or "Holding After Pushback" or "Holding In Position")
            {
                menu.Items.Add(MakeItem("Resume taxi", () => Cmd("RES")));
            }

            if (phase == "LinedUpAndWaiting")
            {
                menu.Items.Add(MakeItem($"Cleared for takeoff{rwy}", () => Cmd("CTO")));
                menu.Items.Add(MakeItem("Cancel takeoff clearance", () => Cmd("CTOC")));
            }

            if (phase == "Takeoff")
            {
                menu.Items.Add(MakeItem("Cancel takeoff clearance", () => Cmd("CTOC")));
            }

            if (phase == "FinalApproach")
            {
                AddLandingItems(menu, ac, vm, callsign, initials);
            }

            if (phase == "Landing")
            {
                menu.Items.Add(MakeItem("Exit left", () => Cmd("EL")));
                menu.Items.Add(MakeItem("Exit right", () => Cmd("ER")));
            }
        }
        else
        {
            if (phase == "FinalApproach")
            {
                AddLandingItems(menu, ac, vm, callsign, initials);
            }
        }
    }

    private static void AddLandingItems(ContextMenu menu, AircraftModel ac, MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);
        var rwy = !string.IsNullOrEmpty(ac.AssignedRunway) ? $" {ac.AssignedRunway}" : "";

        menu.Items.Add(MakeItem($"Cleared to land{rwy}", () => Cmd("CLAND")));
        menu.Items.Add(MakeItem($"Touch and go{rwy}", () => Cmd("TG")));
        menu.Items.Add(MakeItem($"Stop and go{rwy}", () => Cmd("SG")));
        menu.Items.Add(MakeItem($"Low approach{rwy}", () => Cmd("LA")));
        menu.Items.Add(MakeItem($"Cleared for the option{rwy}", () => Cmd("COPT")));
        menu.Items.Add(MakeItem($"Go around{rwy}", () => Cmd("GA")));
        menu.Items.Add(MakeItem("Cancel landing clearance", () => Cmd("CLC")));
    }

    private static MenuItem BuildTrackSubmenu(MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);
        var menu = new MenuItem { Header = "Track" };
        menu.Items.Add(MakeItem("Track", () => Cmd("TRACK")));
        menu.Items.Add(MakeItem("Drop track", () => Cmd("DROP")));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Accept handoff", () => Cmd("ACCEPT")));
        menu.Items.Add(MakeItem("Cancel handoff", () => Cmd("CANCEL")));
        menu.Items.Add(MakeItem("Acknowledge pointout", () => Cmd("OK")));
        return menu;
    }

    private static MenuItem BuildSquawkSubmenu(MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);
        var menu = new MenuItem { Header = "Squawk" };
        menu.Items.Add(MakeItem("Squawk random", () => Cmd("RANDSQ")));
        menu.Items.Add(MakeItem("Squawk VFR", () => Cmd("SQVFR")));
        menu.Items.Add(MakeItem("Squawk normal", () => Cmd("SQNORM")));
        menu.Items.Add(MakeItem("Squawk standby", () => Cmd("SQSBY")));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Ident", () => Cmd("IDENT")));
        return menu;
    }

    private static MenuItem BuildCoordinationSubmenu(MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);
        var menu = new MenuItem { Header = "Coordination" };
        menu.Items.Add(MakeItem("Release", () => Cmd("RD")));
        menu.Items.Add(MakeItem("Hold", () => Cmd("RDH")));
        menu.Items.Add(MakeItem("Recall", () => Cmd("RDR")));
        menu.Items.Add(MakeItem("Acknowledge release", () => Cmd("RDACK")));
        return menu;
    }

    private static MenuItem BuildAskPilotSubmenu(MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);
        var menu = new MenuItem { Header = "Ask pilot to say..." };
        menu.Items.Add(MakeItem("Altitude", () => Cmd("SALT")));
        menu.Items.Add(MakeItem("Heading", () => Cmd("SHDG")));
        menu.Items.Add(MakeItem("Speed", () => Cmd("SSPD")));
        menu.Items.Add(MakeItem("Mach", () => Cmd("SMACH")));
        menu.Items.Add(MakeItem("Position", () => Cmd("SPOS")));
        menu.Items.Add(MakeItem("Expected approach", () => Cmd("SEAPP")));
        return menu;
    }

    private static MenuItem BuildTowerSubmenu(AircraftModel ac, MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);
        var rwy = !string.IsNullOrEmpty(ac.AssignedRunway) ? $" {ac.AssignedRunway}" : "";
        var menu = new MenuItem { Header = "Tower" };
        menu.Items.Add(MakeItem($"Line up and wait{rwy}", () => Cmd("LUAW")));
        menu.Items.Add(MakeItem($"Cleared for takeoff{rwy}", () => Cmd("CTO")));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem($"Cleared to land{rwy}", () => Cmd("CLAND")));
        menu.Items.Add(MakeItem($"Cleared for the option{rwy}", () => Cmd("COPT")));
        menu.Items.Add(MakeItem($"Touch and go{rwy}", () => Cmd("TG")));
        menu.Items.Add(MakeItem($"Stop and go{rwy}", () => Cmd("SG")));
        menu.Items.Add(MakeItem($"Low approach{rwy}", () => Cmd("LA")));
        menu.Items.Add(MakeItem($"Go around{rwy}", () => Cmd("GA")));
        return menu;
    }
}
