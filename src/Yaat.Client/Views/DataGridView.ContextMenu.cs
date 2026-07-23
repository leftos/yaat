using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim.Data.Airport;

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

    internal static void AddPhaseAwareItems(ContextMenu menu, AircraftModel ac, MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);

        var phase = ac.CurrentPhase ?? "";

        // Ground movement (taxi/push/hold) — not covered by the tower applicability predicates
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
                var heldRwy = HoldShortMenuHelper.HeldRunway(phase, ac);
                if (!string.IsNullOrEmpty(heldRwy))
                {
                    menu.Items.Add(MakeItem($"Cross {RunwayIdentifier.ToDisplayDesignator(heldRwy)}", () => Cmd($"CROSS {heldRwy}")));
                }
            }

            if (phase is "Holding After Exit" or "Holding After Pushback" or "Holding In Position")
            {
                menu.Items.Add(MakeItem("Resume taxi", () => Cmd("RES")));
            }
        }

        // Departure clearances. The runway is shown in the label for context, but the
        // command is always the bare verb — LUAW and CTO have no runway argument; the
        // server resolves the departure runway from the aircraft's assigned runway.
        var depRwy = HoldShortMenuHelper.HeldRunway(phase, ac);
        var depRwyLabel = !string.IsNullOrEmpty(depRwy) ? $" {RunwayIdentifier.ToDisplayDesignator(depRwy)}" : "";

        if (AircraftCommandApplicability.CanLineUpAndWait(ac))
        {
            menu.Items.Add(MakeItem($"Line up and wait{depRwyLabel}", () => Cmd("LUAW")));
        }

        if (AircraftCommandApplicability.CanClearForTakeoff(ac))
        {
            menu.Items.Add(MakeItem($"Cleared for takeoff{depRwyLabel}", () => Cmd("CTO")));
        }

        if (AircraftCommandApplicability.CanCancelTakeoff(ac))
        {
            menu.Items.Add(MakeItem("Cancel takeoff clearance", () => Cmd("CTOC")));
        }

        if (ac.CfrWindowStartUtc is not null && ac.IsOnGround)
        {
            menu.Items.Add(MakeItem("Check release window", () => Cmd("CFR CHECK")));
        }

        // Arrival / landing clearances
        if (
            AircraftCommandApplicability.CanClearToLand(ac)
            || AircraftCommandApplicability.CanGoAround(ac)
            || AircraftCommandApplicability.CanCancelLandingClearance(ac)
        )
        {
            AddLandingItems(menu, ac, vm, callsign, initials);
        }

        // Runway exit (after touchdown)
        if (AircraftCommandApplicability.CanExitRunway(ac))
        {
            menu.Items.Add(MakeItem("Exit left", () => Cmd("EL")));
            menu.Items.Add(MakeItem("Exit right", () => Cmd("ER")));
        }
    }

    private static void AddLandingItems(ContextMenu menu, AircraftModel ac, MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);
        var rwy = !string.IsNullOrEmpty(ac.AssignedRunway) ? $" {RunwayIdentifier.ToDisplayDesignator(ac.AssignedRunway)}" : "";

        if (AircraftCommandApplicability.CanClearToLand(ac))
        {
            menu.Items.Add(MakeItem($"Cleared to land{rwy}", () => Cmd("CLAND")));
            // Force landing (CLANDF) is RPO-only — hidden in solo training.
            if (vm.SessionSoloTrainingMode != true)
            {
                menu.Items.Add(MakeItem($"Force landing{rwy}", () => Cmd("CLANDF")));
            }
            if (AircraftCommandApplicability.CanIssueVfrOption(ac))
            {
                menu.Items.Add(MakeItem($"Touch and go{rwy}", () => Cmd("TG")));
                menu.Items.Add(MakeItem($"Stop and go{rwy}", () => Cmd("SG")));
                menu.Items.Add(MakeItem($"Low approach{rwy}", () => Cmd("LA")));
                menu.Items.Add(MakeItem($"Cleared for the option{rwy}", () => Cmd("COPT")));
            }
        }

        if (AircraftCommandApplicability.CanGoAround(ac))
        {
            menu.Items.Add(MakeItem($"Go around{rwy}", () => Cmd("GA")));
        }

        if (AircraftCommandApplicability.CanCancelLandingClearance(ac))
        {
            menu.Items.Add(MakeItem("Cancel landing clearance", () => Cmd("CLC")));
        }
    }

    private static readonly (string Label, int Seconds)[] SpawnDelayPresets =
    [
        ("15 seconds", 15),
        ("30 seconds", 30),
        ("1 minute", 60),
        ("2 minutes", 120),
        ("5 minutes", 300),
        ("10 minutes", 600),
    ];

    private static void AddDelayedSpawnItems(ContextMenu menu, MainViewModel vm, string callsign, string initials)
    {
        Task Cmd(string raw) => vm.Connection.SendCommandAsync(callsign, raw, initials);

        menu.Items.Add(MakeItem("Spawn now", () => Cmd("SPAWN")));

        var delayMenu = new MenuItem { Header = "Change spawn delay" };
        foreach (var (label, seconds) in SpawnDelayPresets)
        {
            delayMenu.Items.Add(MakeItem(label, () => Cmd($"SPAWNDELAY {seconds}")));
        }
        delayMenu.Items.Add(new Separator());
        delayMenu.Items.Add(BuildCustomDelayInput(menu, callsign, vm, initials));
        menu.Items.Add(delayMenu);

        menu.Items.Add(MakeItem("Delete", () => Cmd("DEL")));
    }

    private static TextBox BuildCustomDelayInput(ContextMenu parentMenu, string callsign, MainViewModel vm, string initials)
    {
        var textBox = new TextBox
        {
            PlaceholderText = "Custom (e.g. 90, 2m15s, 1h)",
            FontSize = 12,
            MinWidth = 180,
        };
        textBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            e.Handled = true;
            var seconds = ParseDelayInput(textBox.Text);
            if (seconds is null)
            {
                return;
            }

            parentMenu.Close();
            await vm.Connection.SendCommandAsync(callsign, $"SPAWNDELAY {seconds.Value}", initials);
        };
        return textBox;
    }

    private static readonly Regex DelayUnitsPattern = new(
        @"^\s*(?:(?<h>\d+)\s*h)?\s*(?:(?<m>\d+)\s*m)?\s*(?:(?<s>\d+)\s*s)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    internal static int? ParseDelayInput(string? input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bareSeconds))
        {
            return bareSeconds;
        }

        var match = DelayUnitsPattern.Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var hours = match.Groups["h"];
        var minutes = match.Groups["m"];
        var secs = match.Groups["s"];
        if (!hours.Success && !minutes.Success && !secs.Success)
        {
            return null;
        }

        long total = 0;
        if (hours.Success)
        {
            total += long.Parse(hours.Value, System.Globalization.CultureInfo.InvariantCulture) * 3600L;
        }
        if (minutes.Success)
        {
            total += long.Parse(minutes.Value, System.Globalization.CultureInfo.InvariantCulture) * 60L;
        }
        if (secs.Success)
        {
            total += long.Parse(secs.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (total < 0 || total > int.MaxValue)
        {
            return null;
        }
        return (int)total;
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
}
