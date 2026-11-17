using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// The pick menu a Scope field click opens: a context menu holding the one entry for the field, from the shared menu catalog
/// (<see cref="MenuCatalog"/>) or, for the assigned runway, which the catalog has no picker for, the runway rows built here;
/// the entry's submenu or flyout opens with the menu. Every row sends for the clicked aircraft through the host.
/// </summary>
internal static class ScopeFieldMenus
{
    /// <summary>
    /// The menu <paramref name="action"/> opens over <paramref name="aircraft"/>, or null when the action opens no menu (none,
    /// the note box, take control), its catalog entry does not apply to the aircraft, or the entry has nothing to offer it.
    /// </summary>
    public static ContextMenu? Build(ScopeFieldAction action, AircraftModel aircraft, IMenuHost host)
    {
        var context = new MenuContext(new MenuClick(aircraft.Callsign, null, null, []), host.Session);
        MenuItem? entry =
            action == ScopeFieldAction.OpenRunwayMenu ? RunwayEntry(aircraft, context, host) : CatalogEntry(action, aircraft, context, host);
        if (entry is null)
        {
            return null;
        }

        var menu = new ContextMenu { Items = { entry } };
        menu.Opened += (_, _) => entry.IsSubMenuOpen = true;
        return menu;
    }

    /// <summary>
    /// Calls <paramref name="record"/> with whether Shift is held at each pointer press, pointer release and key press inside
    /// <paramref name="menu"/>: a context menu, its submenu popups included, whose input routes through their parent menu, or a
    /// popup's content, which is its own top level. The handlers tunnel, so the record is made before the row under the pointer
    /// or the focus runs its action.
    /// </summary>
    public static void RecordShift(Control menu, Action<bool> record)
    {
        menu.AddHandler(
            InputElement.PointerPressedEvent,
            (_, e) => record(e.KeyModifiers.HasFlag(KeyModifiers.Shift)),
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        menu.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, e) => record(e.KeyModifiers.HasFlag(KeyModifiers.Shift)),
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        menu.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => record(e.KeyModifiers.HasFlag(KeyModifiers.Shift)),
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
    }

    /// <summary>Opens the instructor note box for <paramref name="aircraft"/>, as the catalog's Note… row does.</summary>
    public static void ShowNoteBox(AircraftModel aircraft, IMenuHost host)
    {
        string callsign = aircraft.Callsign;
        string initials = host.Session.Initials;
        host.ShowNoteFlyout(callsign, aircraft.Note ?? "", command => host.SendAsync(callsign, command, initials));
    }

    // The catalog entry the action opens, built only while it applies to the aircraft.
    private static MenuItem? CatalogEntry(ScopeFieldAction action, AircraftModel aircraft, MenuContext context, IMenuHost host)
    {
        if (CatalogIdFor(action, aircraft, context) is not { } id)
        {
            return null;
        }

        MenuCatalogEntry entry = MenuCatalog.Get(id);
        return entry.IsApplicable(aircraft, context) ? entry.Build(aircraft, context, host) : null;
    }

    /// <summary>
    /// The handoff entry for the track's state as the acting position sees it, by the quick list's checks: accept a handoff in
    /// progress to it, else cancel one from it, else initiate one on an owned track with none; else none (with no acting
    /// position, Accept and Cancel never show, as in the right-click menu).
    /// </summary>
    private static string? HandoffIdFor(AircraftModel aircraft, MenuContext context)
    {
        if (AircraftCommandApplicability.ShowsAcceptHandoff(aircraft, context))
        {
            return MenuIds.TrackAcceptHandoff;
        }

        if (AircraftCommandApplicability.ShowsCancelHandoff(aircraft, context))
        {
            return MenuIds.TrackCancelHandoff;
        }

        bool initiate = AircraftCommandApplicability.IsOwned(aircraft) && !AircraftCommandApplicability.HasHandoffInProgress(aircraft);
        return initiate ? MenuIds.TrackInitiateHandoff : null;
    }

    private static string? CatalogIdFor(ScopeFieldAction action, AircraftModel aircraft, MenuContext context) =>
        action switch
        {
            ScopeFieldAction.OpenAltitudeMenu => MenuIds.AltitudeMaintain,
            ScopeFieldAction.OpenSpeedMenu => MenuIds.SpeedAssign,
            ScopeFieldAction.OpenSquawkMenu => MenuIds.SquawkCode,
            ScopeFieldAction.OpenScratchpadMenu => MenuIds.DataBlockScratchpad,
            ScopeFieldAction.OpenHandoffMenu => HandoffIdFor(aircraft, context),
            _ => null,
        };

    /// <summary>
    /// The runway rows the Radar's runway picker offers: clear the assignment, then every runway end at the airport
    /// (<see cref="AirportFor"/>), each sending <c>RWY</c> and its designator.
    /// </summary>
    private static MenuItem RunwayEntry(AircraftModel aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = "Runway" };
        if (!string.IsNullOrEmpty(aircraft.AssignedRunway))
        {
            entry.Items.Add(Caption($"Currently assigned: {RunwayIdentifier.ToDisplayDesignator(aircraft.AssignedRunway)}"));
        }

        entry.Items.Add(MenuCatalog.BuildSend("Clear assigned runway", "RWY", context, host));
        string airport = AirportFor(aircraft);
        List<string> ends = string.IsNullOrEmpty(airport) ? [] : RunwayEnds(airport);
        if (ends.Count > 0)
        {
            entry.Items.Add(new Separator());
            entry.Items.Add(Caption($"{airport} runways ({(aircraft.IsOnGround ? "departure" : "approach")})"));
            foreach (string end in ends)
            {
                entry.Items.Add(RunwayRow(aircraft, end, context, host));
            }
        }

        return entry;
    }

    /// <summary>The departure airport on the ground, the destination airborne; the other when that one is empty.</summary>
    private static string AirportFor(AircraftModel aircraft)
    {
        string primary = aircraft.IsOnGround ? aircraft.Departure : aircraft.Destination;
        string fallback = aircraft.IsOnGround ? aircraft.Destination : aircraft.Departure;
        return !string.IsNullOrEmpty(primary) ? primary : fallback;
    }

    /// <summary>The row sending <c>RWY</c> and <paramref name="end"/>, marked <c>▶</c> when it is the assigned runway.</summary>
    private static MenuItem RunwayRow(AircraftModel aircraft, string end, MenuContext context, IMenuHost host)
    {
        string display = RunwayIdentifier.ToDisplayDesignator(end);
        MenuItem row = MenuCatalog.BuildSend(display, $"RWY {end}", context, host);
        if (string.Equals(aircraft.AssignedRunway, end, StringComparison.OrdinalIgnoreCase))
        {
            row.Header = $"▶ {display}";
        }
        return row;
    }

    private static MenuItem Caption(string text) =>
        new()
        {
            Header = text,
            IsEnabled = false,
            FontStyle = FontStyle.Italic,
        };

    /// <summary>The airport's runway-end designators, both ends of each runway, sorted numerically.</summary>
    private static List<string> RunwayEnds(string airport)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RunwayInfo runway in NavigationDatabase.Instance.GetRunways(airport))
        {
            foreach (string end in (string[])[runway.Id.End1, runway.Id.End2])
            {
                if (!string.IsNullOrEmpty(end))
                {
                    unique.Add(end);
                }
            }
        }
        return [.. unique.OrderBy(d => d, RunwayDesignatorComparer.Instance)];
    }
}
