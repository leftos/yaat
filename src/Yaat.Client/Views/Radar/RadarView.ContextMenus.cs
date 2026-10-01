using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Radar.Flyouts;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Mva;
using Yaat.Sim.Phases;

namespace Yaat.Client.Views.Radar;

/// <summary>
/// Context menu builders for aircraft and map right-clicks.
/// </summary>
public partial class RadarView
{
    private void OnAircraftLeftClicked(string callsign)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        AircraftModel? ac = FindMainViewModel()?.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null)
        {
            vm.SelectedAircraft = ac;
        }
    }

    private void OnAircraftCtrlClicked(string callsign)
    {
        MainViewModel? mainVm = FindMainViewModel();
        if (mainVm is null)
        {
            return;
        }

        AircraftModel? ac = mainVm.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null)
        {
            FlightPlanEditorManager.Open(ac, mainVm);
        }
    }

    private void OnEmptySpaceClicked()
    {
        if (DataContext is RadarViewModel vm)
        {
            vm.SelectedAircraft = null;
        }
    }

    private void OnAircraftRightClicked(string callsign, Point screenPos)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        AircraftModel? ac = FindMainViewModel()?.Aircraft.FirstOrDefault(a => a.Callsign == callsign);

        // Keep the previously-selected aircraft as the command recipient when the
        // controller right-clicks a DIFFERENT aircraft, so selected→right-clicked
        // relative actions (RTIS / FOLLOW) target the selected aircraft. Only adopt
        // the right-clicked aircraft as the selection when nothing was selected or
        // the same aircraft was re-clicked. Left-click remains the way to change selection.
        AircraftModel? prevSelected = vm.SelectedAircraft;
        if (ac is not null && (prevSelected is null || string.Equals(prevSelected.Callsign, callsign, StringComparison.OrdinalIgnoreCase)))
        {
            vm.SelectedAircraft = ac;
        }

        ShowContextMenu(BuildAircraftContextMenu(vm, ac, prevSelected, callsign, GetInitials()));
    }

    /// <summary>The bold callsign header, route and hold status, the release items, the free-text Command… and the favorites block.</summary>
    private void AddAircraftMenuHeader(ContextMenu menu, RadarViewModel vm, AircraftModel? ac, MenuContext context, RadarMenuHost host)
    {
        string callsign = context.Callsign;
        string initials = context.Initials;
        string typeText = ac is not null ? $"{callsign} - {ac.DisplayAircraftType}" : callsign;
        menu.Items.Add(
            new MenuItem
            {
                Header = typeText,
                IsEnabled = false,
                FontWeight = Avalonia.Media.FontWeight.Bold,
            }
        );
        if (ac is not null)
        {
            MenuItem? routeItem = BuildRouteSummaryItem(ac);
            if (routeItem is not null)
            {
                menu.Items.Add(routeItem);
            }
            MenuItem? holdItem = BuildHoldStatusItem(ac);
            if (holdItem is not null)
            {
                menu.Items.Add(holdItem);
            }
            if (ac.IsHeldForRelease)
            {
                menu.Items.Add(CreateMenuItem($"Release {callsign} (HFR)", () => vm.SendRawCommandAsync(callsign, initials, $"REL {callsign}")));
            }
            if (ac.CfrWindowStartUtc is not null && ac.IsOnGround)
            {
                menu.Items.Add(CreateMenuItem($"Check {callsign} release window", () => vm.SendRawCommandAsync(callsign, initials, "CFR CHECK")));
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(
            CreateMenuItem(
                "Command…",
                () =>
                {
                    // Free-text: the RPO types arbitrary canonical, so it goes through the VFR gate like typed input.
                    MainViewModel? mainVm = FindMainViewModel();
                    CommandFlyout.Open(
                        _canvas!,
                        callsign,
                        cmd =>
                            mainVm is not null
                                ? mainVm.SendGatedCommandForViewAsync(ac, callsign, cmd, initials)
                                : vm.SendRawCommandAsync(callsign, initials, cmd)
                    );
                    return Task.CompletedTask;
                }
            )
        );
        menu.Items.Add(new Separator());

        menu.Items.Add(SharedMenuGroups.Favorites(ac, context, host));
        menu.Items.Add(new Separator());
    }

    /// <summary>
    /// The whole aircraft context menu a right-click shows, built without opening it: everything the handler
    /// resolved first (<paramref name="ac"/>, the selected aircraft <paramref name="prevSelected"/> that
    /// relative actions target, the callsign and the initials). Touches no canvas, popup or pointer state.
    /// </summary>
    internal ContextMenu BuildAircraftContextMenu(RadarViewModel vm, AircraftModel? ac, AircraftModel? prevSelected, string callsign, string initials)
    {
        MainViewModel? main = FindMainViewModel();
        var context = new MenuContext(
            callsign,
            initials,
            prevSelected,
            main?.SessionSoloTrainingMode ?? false,
            main?.VfrCommandsForIfr ?? VfrCommandsForIfr.EnterFinalOnly
        );
        var host = new RadarMenuHost(this, vm, main, ac);
        var menu = new ContextMenu();
        AddAircraftMenuHeader(menu, vm, ac, context, host);

        if (ac is { IsLiveTraffic: true })
        {
            if (AircraftCommandApplicability.CanAssume(ac))
            {
                SharedMenuGroups.AddLiveTrafficAssume(menu.Items, ac, context, host);
                // An assumable shadow then gets the same items a simulated aircraft gets: a command sent to an
                // airborne shadow auto-assumes it server-side, so they apply as they are — minus the ask-pilot
                // queries, which the server refuses for a shadow.
                menu.Items.Add(new Separator());
            }
            else
            {
                AddSurfaceShadowItems(menu, ac, context, host);
                return menu;
            }
        }

        AddRelativeTrafficItems(menu, vm, prevSelected, callsign, initials);
        AddAircraftCommandGroups(menu, vm, ac, context, host);
        return menu;
    }

    /// <summary>A surface live-traffic shadow is never assumable: its menu is read-only — the display groups and a Delete.</summary>
    private void AddSurfaceShadowItems(ContextMenu menu, AircraftModel ac, MenuContext context, RadarMenuHost host)
    {
        string callsign = context.Callsign;
        menu.Items.Add(SharedMenuGroups.Track(ac, context, host, MenuView.Radar));
        menu.Items.Add(SharedMenuGroups.DataBlock(ac, context, host));
        menu.Items.Add(SharedMenuGroups.Coordination(ac, context, host));
        menu.Items.Add(SharedMenuGroups.Display(ac, context, host));
        menu.Items.Add(new Separator());
        menu.Items.Add(SharedMenuGroups.Delete(ac, context, host));
        FindMainViewModel()?.BuildRpoMenuItems(menu, [callsign]);
    }

    /// <summary>
    /// The phase-aware command groups, the always-visible track / data block / squawk / coordination / display
    /// submenus and the Sim Control submenu, exactly as a simulated aircraft gets them. For a live-traffic shadow
    /// the read-only ask-pilot queries stay out (<see cref="AircraftCommandApplicability.CanAskPilot"/>); everything
    /// else, Warp included, applies, because it goes through the command path and so auto-assumes the shadow first.
    /// </summary>
    private void AddAircraftCommandGroups(ContextMenu menu, RadarViewModel vm, AircraftModel? ac, MenuContext context, RadarMenuHost host)
    {
        string callsign = context.Callsign;
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(ac?.CurrentPhase, ac?.IsOnGround ?? false);

        foreach (MenuGroup group in profile.PrimaryGroups)
        {
            AddMenuGroup(menu, group, vm, ac, context, host);
        }

        if (profile.PrimaryGroups.Count > 0 && profile.SecondaryGroups.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        foreach (MenuGroup group in profile.SecondaryGroups)
        {
            AddMenuGroup(menu, group, vm, ac, context, host);
        }

        // Always-visible groups
        menu.Items.Add(new Separator());
        menu.Items.Add(SharedMenuGroups.Track(ac, context, host, MenuView.Radar));
        menu.Items.Add(SharedMenuGroups.DataBlock(ac, context, host));
        menu.Items.Add(SharedMenuGroups.Squawk(ac, context, host, MenuView.Radar));
        if (AircraftCommandApplicability.CanAskPilot(ac))
        {
            menu.Items.Add(SharedMenuGroups.AskPilot(ac, context, host, MenuView.Radar));
        }

        menu.Items.Add(SharedMenuGroups.Coordination(ac, context, host));
        menu.Items.Add(SharedMenuGroups.Display(ac, context, host));
        menu.Items.Add(new Separator());
        menu.Items.Add(SharedMenuGroups.SimControl(ac, context, host));

        // RPO control
        FindMainViewModel()?.BuildRpoMenuItems(menu, [callsign]);
    }

    /// <summary>
    /// When a different aircraft is selected, adds traffic actions issued to that
    /// selected aircraft referencing the right-clicked aircraft: "report in sight"
    /// (RTIS, always offered) and "follow" (only once the selected aircraft has
    /// reported the right-clicked traffic in sight). No-op when no different aircraft
    /// is selected.
    /// </summary>
    private static void AddRelativeTrafficItems(ContextMenu menu, RadarViewModel vm, AircraftModel? selected, string callsign, string initials)
    {
        if (!RelativeTrafficActions.HasRelativeContext(selected, callsign))
        {
            return;
        }

        string a = selected!.Callsign;
        menu.Items.Add(
            new MenuItem
            {
                Header = $"↪ {a}:",
                IsEnabled = false,
                FontWeight = Avalonia.Media.FontWeight.Bold,
            }
        );
        menu.Items.Add(CreateMenuItem($"{a}: report {callsign} in sight", () => vm.ReportTrafficInSightAsync(a, initials, callsign)));
        if (RelativeTrafficActions.ShouldOfferFollow(selected, callsign))
        {
            menu.Items.Add(CreateMenuItem($"{a}: follow {callsign}", () => vm.SendRawCommandAsync(a, initials, $"FOLLOW {callsign}")));
        }
        menu.Items.Add(new Separator());
    }

    private static MenuItem? BuildRouteSummaryItem(AircraftModel ac)
    {
        if (ac.NavigationRoute.Count == 0)
        {
            return null;
        }

        var fixes = new List<string>();
        bool started = string.IsNullOrEmpty(ac.NavigatingTo);
        foreach (string fix in ac.NavigationRoute)
        {
            if (!started && fix == ac.NavigatingTo)
            {
                started = true;
            }

            if (started)
            {
                fixes.Add(fix);
            }
        }

        if (fixes.Count == 0)
        {
            return null;
        }

        const int maxDisplay = 5;
        string displayFixes = fixes.Count > maxDisplay ? string.Join(" ", fixes.Take(maxDisplay)) + " ..." : string.Join(" ", fixes);
        string fullRoute = string.Join(" ", fixes);

        var item = new MenuItem
        {
            Header = displayFixes,
            IsEnabled = false,
            FontSize = 11,
            Opacity = 0.8,
        };
        ToolTip.SetTip(item, fullRoute);
        ToolTip.SetShowDelay(item, 0);

        return item;
    }

    /// <summary>
    /// Header-strip item that surfaces an active ground hold ("Held: position" for
    /// HOLDPOSITION; "Yielding to {target}" for GIVEWAY). Non-clickable, italicised
    /// so it visually reads as status rather than as a command. Null when the
    /// aircraft is not held.
    /// </summary>
    private static MenuItem? BuildHoldStatusItem(AircraftModel ac)
    {
        if (!ac.IsHeld && string.IsNullOrEmpty(ac.AutoYieldTarget))
        {
            return null;
        }

        string header = ac.HoldKind switch
        {
            "GiveWay" when !string.IsNullOrEmpty(ac.HoldYieldTarget) => $"Yielding to: {ac.HoldYieldTarget}",
            "HoldPosition" => "Held: position",
            _ when !string.IsNullOrEmpty(ac.AutoYieldTarget) && ac.AutoYieldIsFollowing => $"Following: {ac.AutoYieldTarget} (auto-detected)",
            _ when !string.IsNullOrEmpty(ac.AutoYieldTarget) => $"Yielding to: {ac.AutoYieldTarget} (auto-detected)",
            _ => "Held",
        };

        var item = new MenuItem
        {
            Header = header,
            IsEnabled = false,
            FontSize = 11,
            FontStyle = Avalonia.Media.FontStyle.Italic,
            Opacity = 0.85,
        };
        return item;
    }

    /// <summary>
    /// Builds the state-aware Tower submenu. Departure clearances appear only for ground
    /// departures, arrival/option clearances only while a landing is pending (VFR options
    /// hidden for IFR), runway-exit items only after touchdown. Returns null when nothing
    /// applies so the caller can omit the submenu entirely.
    /// </summary>
    internal MenuItem? BuildTowerSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Tower" };
        string rwy = !string.IsNullOrEmpty(ac?.AssignedRunway) ? $" {RunwayIdentifier.ToDisplayDesignator(ac.AssignedRunway)}" : "";

        // Departures
        if (AircraftCommandApplicability.CanLineUpAndWait(ac))
        {
            menu.Items.Add(CreateMenuItem($"Line up and wait{rwy}", () => vm.LineUpAndWaitAsync(cs, init)));
        }
        if (AircraftCommandApplicability.CanClearForTakeoff(ac))
        {
            menu.Items.Add(BuildClearedForTakeoffSubmenu(vm, cs, init, ac));
        }
        if (AircraftCommandApplicability.CanCancelTakeoff(ac))
        {
            menu.Items.Add(CreateMenuItem("Cancel takeoff clearance", () => vm.CancelTakeoffClearanceAsync(cs, init)));
        }

        // Arrivals / pattern landing
        bool canLand = AircraftCommandApplicability.CanClearToLand(ac);
        bool canGoAround = AircraftCommandApplicability.CanGoAround(ac);
        bool canCancelLanding = AircraftCommandApplicability.CanCancelLandingClearance(ac);
        if (canLand || canGoAround || canCancelLanding)
        {
            AddSeparatorIfNonEmpty(menu);
            if (canLand)
            {
                menu.Items.Add(CreateMenuItem($"Cleared to land{rwy}", () => vm.ClearedToLandAsync(cs, init)));
                // Force landing (CLANDF) is an RPO-only override — hidden in solo training, where
                // the server rejects it. Forces a touchdown regardless of energy state.
                if (FindMainViewModel()?.SessionSoloTrainingMode != true)
                {
                    menu.Items.Add(CreateMenuItem($"Force landing{rwy}", () => vm.ForceLandingAsync(cs, init)));
                }
                if (AircraftCommandApplicability.CanIssueVfrOption(ac, VfrCommandsForIfrMode()))
                {
                    menu.Items.Add(CreateMenuItem($"Cleared for the option{rwy}", () => vm.ClearedForOptionAsync(cs, init)));
                    menu.Items.Add(CreateMenuItem($"Touch and go{rwy}", () => vm.TouchAndGoAsync(cs, init)));
                    menu.Items.Add(CreateMenuItem($"Stop and go{rwy}", () => vm.StopAndGoAsync(cs, init)));
                    menu.Items.Add(CreateMenuItem($"Low approach{rwy}", () => vm.LowApproachAsync(cs, init)));
                }
            }
            if (canGoAround)
            {
                menu.Items.Add(CreateMenuItem($"Go around{rwy}", () => vm.GoAroundAsync(cs, init)));
            }
            if (canCancelLanding)
            {
                menu.Items.Add(CreateMenuItem("Cancel landing clearance", () => vm.CancelLandingClearanceAsync(cs, init)));
            }
        }

        // Runway exit (after touchdown)
        if (AircraftCommandApplicability.CanExitRunway(ac))
        {
            AddSeparatorIfNonEmpty(menu);
            menu.Items.Add(CreateMenuItem("Exit left", () => vm.ExitLeftAsync(cs, init)));
            menu.Items.Add(CreateMenuItem("Exit right", () => vm.ExitRightAsync(cs, init)));
        }

        return menu.Items.Count > 0 ? menu : null;
    }

    private static void AddSeparatorIfNonEmpty(MenuItem menu)
    {
        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }
    }

    private MenuItem BuildClearedForTakeoffSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Cleared for takeoff" };

        // Default clearance: IFR follows the filed SID, VFR flies runway heading.
        menu.Items.Add(CreateMenuItem("Default (SID/on course)", () => vm.ClearedForTakeoffAsync(cs, init, null)));
        // Explicit runway heading — valid for both VFR and IFR (issue #221).
        menu.Items.Add(CreateMenuItem("Fly runway heading", () => vm.ClearedForTakeoffAsync(cs, init, "RH")));

        // On-course, pattern, and closed-traffic modifiers are VFR-only — offered for an IFR
        // departure only when the controller opted into the full VFR command set.
        if (AircraftCommandApplicability.ShowVfrTakeoffModifiers(ac, VfrCommandsForIfrMode()))
        {
            menu.Items.Add(CreateMenuItem("Fly on course", () => vm.ClearedForTakeoffAsync(cs, init, "OC")));
            menu.Items.Add(CreateMenuItem("Make left traffic", () => vm.ClearedForTakeoffAsync(cs, init, "MLT")));
            menu.Items.Add(CreateMenuItem("Make right traffic", () => vm.ClearedForTakeoffAsync(cs, init, "MRT")));
            menu.Items.Add(CreateMenuItem("Turn left crosswind", () => vm.ClearedForTakeoffAsync(cs, init, "MLC")));
            menu.Items.Add(CreateMenuItem("Turn right crosswind", () => vm.ClearedForTakeoffAsync(cs, init, "MRC")));
            menu.Items.Add(CreateMenuItem("Turn left downwind", () => vm.ClearedForTakeoffAsync(cs, init, "MLD")));
            menu.Items.Add(CreateMenuItem("Turn right downwind", () => vm.ClearedForTakeoffAsync(cs, init, "MRD")));
            menu.Items.Add(CreateMenuItem("Left 270", () => vm.ClearedForTakeoffAsync(cs, init, "ML270")));
            menu.Items.Add(CreateMenuItem("Right 270", () => vm.ClearedForTakeoffAsync(cs, init, "MR270")));
            menu.Items.Add(CreateMenuItem("360 overhead", () => vm.ClearedForTakeoffAsync(cs, init, "360")));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(
            CreateInputMenuItem(
                "Custom...",
                "CTO arg (e.g. RH 3000, LT 270, DCT BERKS)",
                input => vm.ClearedForTakeoffAsync(cs, init, NullIfEmpty(input))
            )
        );

        return menu;
    }

    private void AddMenuGroup(ContextMenu menu, MenuGroup group, RadarViewModel vm, AircraftModel? ac, MenuContext context, RadarMenuHost host)
    {
        string cs = context.Callsign;
        string init = context.Initials;
        switch (group)
        {
            case MenuGroup.Heading:
                menu.Items.Add(SharedMenuGroups.Heading(ac, context, host));
                break;
            case MenuGroup.Altitude:
                menu.Items.Add(SharedMenuGroups.Altitude(ac, context, host));
                break;
            case MenuGroup.Speed:
                menu.Items.Add(SharedMenuGroups.Speed(ac, context, host));
                break;
            case MenuGroup.Navigation:
                menu.Items.Add(SharedMenuGroups.Navigation(ac, context, host));
                break;
            case MenuGroup.DrawRoute:
                menu.Items.Add(SharedMenuGroups.DrawRoute(ac, context, host));
                break;
            case MenuGroup.Hold:
                menu.Items.Add(SharedMenuGroups.Hold(ac, context, host));
                break;
            case MenuGroup.Approach:
                menu.Items.Add(SharedMenuGroups.Approach(ac, context, host));
                break;
            case MenuGroup.Procedures:
                menu.Items.Add(SharedMenuGroups.Procedures(ac, context, host));
                break;
            case MenuGroup.Tower:
                MenuItem? tower = BuildTowerSubmenu(vm, cs, init, ac);
                if (tower is not null)
                {
                    menu.Items.Add(tower);
                }
                break;
            case MenuGroup.Pattern:
                MenuItem? pattern = BuildPatternSubmenu(vm, cs, init, ac);
                if (pattern is not null)
                {
                    menu.Items.Add(pattern);
                }
                break;
        }
    }

    /// <summary>
    /// Builds the Pattern submenu. Entries are offered to airborne aircraft being sequenced in;
    /// maneuvers are leg-specific (turn-crosswind only from upwind, etc.). Pattern operations are
    /// VFR-only unless the controller's "VFR commands for IFR aircraft" setting opens them up —
    /// straight-in final under the default setting, the rest only under the full one. Returns null
    /// when nothing applies.
    /// </summary>
    internal MenuItem? BuildPatternSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        VfrCommandsForIfr mode = VfrCommandsForIfrMode();
        var menu = new MenuItem { Header = "Pattern" };
        AddPatternEntryItems(menu, vm, cs, init, ac, mode);
        AddPatternManeuverItems(menu, vm, cs, init, ac, mode);
        return menu.Items.Count > 0 ? menu : null;
    }

    private void AddPatternManeuverItems(MenuItem menu, RadarViewModel vm, string cs, string init, AircraftModel? ac, VfrCommandsForIfr mode)
    {
        // Pattern maneuvers are valid only from specific legs of the circuit.
        if (!AircraftCommandApplicability.CanIssuePatternManeuvers(ac, mode))
        {
            return;
        }

        string phase = ac?.CurrentPhase ?? "";

        // Leg turns — each valid only from the preceding leg
        var turns = new List<MenuItem>();
        if (phase == "Upwind")
        {
            turns.Add(CreateMenuItem("Turn crosswind", () => vm.TurnCrosswindAsync(cs, init)));
        }
        if (phase == "Crosswind")
        {
            turns.Add(CreateMenuItem("Turn downwind", () => vm.TurnDownwindAsync(cs, init)));
        }
        if (phase == "Downwind")
        {
            turns.Add(CreateMenuItem("Turn base", () => vm.TurnBaseAsync(cs, init)));
        }
        AddManeuverGroup(menu, turns);

        // Spacing adjustments
        var spacing = new List<MenuItem>();
        if (phase is "Upwind" or "Crosswind" or "Downwind")
        {
            spacing.Add(CreateMenuItem("Extend pattern leg", () => vm.ExtendPatternAsync(cs, init)));
        }
        if (phase is "Downwind" or "Base")
        {
            spacing.Add(CreateMenuItem("Make short approach", () => vm.MakeShortApproachAsync(cs, init)));
            spacing.Add(CreateMenuItem("Make normal approach", () => vm.MakeNormalApproachAsync(cs, init)));
        }
        AddManeuverGroup(menu, spacing);

        // 360 / 270 orbits — any pattern leg
        var orbits = new List<MenuItem>();
        if (AircraftCommandApplicability.IsPatternPhase(phase))
        {
            orbits.Add(CreateMenuItem("Make left 360", () => vm.MakeLeft360Async(cs, init)));
            orbits.Add(CreateMenuItem("Make right 360", () => vm.MakeRight360Async(cs, init)));
            orbits.Add(CreateMenuItem("Make left 270", () => vm.MakeLeft270Async(cs, init)));
            orbits.Add(CreateMenuItem("Make right 270", () => vm.MakeRight270Async(cs, init)));
        }
        if (phase is "Upwind" or "Crosswind" or "Downwind" or "Base")
        {
            orbits.Add(CreateMenuItem("Plan 270 at next turn", () => vm.Plan270Async(cs, init)));
            orbits.Add(CreateMenuItem("Cancel 270", () => vm.Cancel270Async(cs, init)));
        }
        AddManeuverGroup(menu, orbits);

        // Circle the airport — any pattern leg
        if (AircraftCommandApplicability.IsPatternPhase(phase))
        {
            AddManeuverGroup(menu, [CreateMenuItem("Circle airport", () => vm.CircleAirportAsync(cs, init))]);
        }
    }

    private static void AddManeuverGroup(MenuItem menu, List<MenuItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        AddSeparatorIfNonEmpty(menu);
        foreach (MenuItem item in items)
        {
            menu.Items.Add(item);
        }
    }

    private void AddPatternEntryItems(MenuItem menu, RadarViewModel vm, string cs, string init, AircraftModel? ac, VfrCommandsForIfr mode)
    {
        bool circuitLegs = AircraftCommandApplicability.CanEnterPattern(ac, mode);
        bool straightIn = AircraftCommandApplicability.CanEnterFinal(ac, mode);
        if (!circuitLegs && !straightIn)
        {
            return;
        }

        string? runwayAirport = ac is not null ? (!string.IsNullOrEmpty(ac.Destination) ? ac.Destination : ac.Departure) : null;
        IReadOnlyList<string> runways = !string.IsNullOrEmpty(runwayAirport) ? RunwayDesignators.ForAirport(runwayAirport) : [];
        string? defaultRunway = !string.IsNullOrEmpty(ac?.AssignedRunway) ? ac.AssignedRunway : null;

        if (circuitLegs)
        {
            AddPatternEntry(menu, "Enter left downwind", runways, defaultRunway, rwy => vm.EnterLeftDownwindAsync(cs, init, rwy));
            AddPatternEntry(menu, "Enter right downwind", runways, defaultRunway, rwy => vm.EnterRightDownwindAsync(cs, init, rwy));
            AddPatternEntry(menu, "Enter left base", runways, defaultRunway, rwy => vm.EnterLeftBaseAsync(cs, init, rwy));
            AddPatternEntry(menu, "Enter right base", runways, defaultRunway, rwy => vm.EnterRightBaseAsync(cs, init, rwy));
        }

        if (straightIn)
        {
            AddPatternEntry(menu, "Enter straight-in final", runways, defaultRunway, rwy => vm.EnterFinalAsync(cs, init, rwy));
        }
    }

    private void AddPatternEntry(MenuItem menu, string baseLabel, IReadOnlyList<string> runways, string? defaultRunway, Func<string?, Task> action)
    {
        if (defaultRunway is not null)
        {
            menu.Items.Add(CreateMenuItem($"{baseLabel} {RunwayIdentifier.ToDisplayDesignator(defaultRunway)}", () => action(defaultRunway)));
        }

        if (runways.Count > 0)
        {
            string label = defaultRunway is not null ? $"{baseLabel} (other)..." : $"{baseLabel}...";
            var items = runways.Cast<object>().ToList();
            menu.Items.Add(CreateListMenuItem(label, items, items[0], val => action((string)val)));
        }
        else if (defaultRunway is null)
        {
            menu.Items.Add(CreateInputMenuItem($"{baseLabel}...", "Runway (optional)", input => action(NullIfEmpty(input))));
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private void OnMapRightClicked(double lat, double lon, Point screenPos)
    {
        if (DataContext is not RadarViewModel vm)
        {
            return;
        }

        var menu = new ContextMenu();

        // FRD header — always show regardless of aircraft selection
        string? frdString = null;
        if (vm.Fixes is not null)
        {
            frdString = FrdResolver.ToFrd(lat, lon, vm.Fixes);
        }

        if (frdString is not null)
        {
            menu.Items.Add(
                new MenuItem
                {
                    Header = frdString,
                    IsEnabled = false,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                }
            );
            string frd = frdString;
            menu.Items.Add(
                CreateMenuItem(
                    "Copy FRD",
                    async () =>
                    {
                        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                        if (clipboard is not null)
                        {
                            await clipboard.SetTextAsync(frd);
                        }
                    }
                )
            );

            // Scope-marker pins (CRC ".ff"/".marker"). Pick radius scales with the current range.
            double pickNm = Math.Max(0.5, vm.RangeNm * 0.04);
            var clickPos = new LatLon(lat, lon);
            bool nearPin = vm.PinnedMarkers is { } pins && pins.Any(m => GeoMath.DistanceNm(clickPos, new LatLon(m.Lat, m.Lon)) <= pickNm);

            menu.Items.Add(CreateMenuItem("Pin marker here", () => vm.AddMarker(frd)));
            if (nearPin)
            {
                menu.Items.Add(CreateMenuItem("Remove marker", () => vm.RemoveNearestMarker(lat, lon, pickNm)));
            }
            if (vm.HasMarkers)
            {
                menu.Items.Add(CreateMenuItem("Clear pinned markers", () => vm.ClearMarkers()));
            }

            menu.Items.Add(new Separator());
        }

        AddMeasureMenuItems(menu, vm, RblEndpoint.AtPoint(new LatLon(lat, lon), frdString ?? ""), screenPos);

        // MVA at the clicked point (FAA-charted; only the loaded facility's coverage, null elsewhere).
        MvaSector? mvaSector = MvaDatabase.Default.FindSector(new LatLon(lat, lon));
        menu.Items.Add(
            new MenuItem
            {
                Header = mvaSector is null ? "MVA: no data here" : $"MVA {mvaSector.FloorFtMsl} ft ({mvaSector.Sector})",
                IsEnabled = false,
            }
        );
        menu.Items.Add(new Separator());

        if (vm.SelectedAircraft is not null)
        {
            string callsign = vm.SelectedAircraft.Callsign;
            string initials = GetInitials();

            int heading = (int)(Math.Round(GeoMath.BearingTo(vm.SelectedAircraft.Position, new LatLon(lat, lon)) / 5.0) * 5);
            if (heading <= 0)
            {
                heading = 360;
            }

            menu.Items.Add(
                CreateMenuItem($"Fly heading {new MagneticHeading(heading).ToDisplayString()}", () => vm.FlyHeadingAsync(callsign, initials, heading))
            );

            if (frdString is not null)
            {
                string target = frdString;
                menu.Items.Add(CreateMenuItem($"Direct to {target}", () => vm.DirectToAsync(callsign, initials, target)));
                menu.Items.Add(CreateMenuItem($"Append direct to {target}", () => vm.AppendDirectToAsync(callsign, initials, target)));
                menu.Items.Add(CreateMenuItem($"Hold at {target} (left)", () => vm.HoldAtFixLeftAsync(callsign, initials, target)));
                menu.Items.Add(CreateMenuItem($"Hold at {target} (right)", () => vm.HoldAtFixRightAsync(callsign, initials, target)));

                string warpFrd = target;
                int warpHdg = (int)Math.Round(vm.SelectedAircraft.Heading.Degrees);
                if (warpHdg <= 0)
                {
                    warpHdg = 360;
                }

                int warpAlt = (int)Math.Round(vm.SelectedAircraft.Altitude);
                int warpSpd = (int)Math.Round(vm.SelectedAircraft.IndicatedAirspeed);
                var warpItem = new MenuItem { Header = $"Warp here ({target})" };
                warpItem.Click += (_, _) =>
                {
                    Dispatcher.UIThread.Post(() =>
                        ShowWarpPopup(
                            callsign,
                            warpFrd,
                            warpHdg,
                            warpAlt,
                            warpSpd,
                            (frd, h, a, s) => _ = vm.WarpAsync(callsign, initials, frd, h, a, s)
                        )
                    );
                };
                menu.Items.Add(warpItem);
            }
        }

        if (menu.Items.Count > 0)
        {
            ShowContextMenu(menu);
        }
    }

    // --- Menu item factories ---

    private static MenuItem CreateMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Adds the distance measuring tool's items: start or finish a measurement at
    /// <paramref name="endpoint" />, remove the one under the cursor, and clear them all.
    /// </summary>
    private void AddMeasureMenuItems(ContextMenu menu, RadarViewModel vm, RblEndpoint endpoint, Point screenPos)
    {
        if (vm.Measure is not { } measure)
        {
            return;
        }

        string startLabel = measure.Anchor is null ? "Measure from here" : "Measure to here";
        menu.Items.Add(
            CreateMenuItem(startLabel, () => measure.Pick(endpoint, RadarViewModel.MeasureView, vm.MeasureTrackLookup, RadarViewModel.MeasureUnits))
        );

        if (_canvas?.MeasurementSlotAt(screenPos) is { } slot)
        {
            menu.Items.Add(CreateMenuItem($"Remove measurement {slot}", () => measure.Remove(slot)));
        }

        if (measure.HasLines)
        {
            menu.Items.Add(CreateMenuItem("Clear measurements", measure.Clear));
        }

        menu.Items.Add(new Separator());
    }

    private MenuItem CreateInputMenuItem(string header, string placeholder, Func<string, Task> action)
    {
        var item = new MenuItem { Header = header, Tag = new MenuPickerDescriptor(MenuPickerDescriptor.Input, []) };
        item.Click += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => ShowInputPopup(placeholder, action));
        };
        return item;
    }

    private MenuItem CreateListMenuItem(string header, IReadOnlyList<object> items, object? selectedValue, Func<object, Task> action)
    {
        var item = new MenuItem { Header = header, Tag = new MenuPickerDescriptor(MenuPickerDescriptor.List, ListPickerTexts(items)) };
        item.Click += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => ShowListPopup(items, selectedValue, action));
        };
        return item;
    }

    /// <summary>The display texts of a popup's values, as the popup itself shows them.</summary>
    private static List<string> ListPickerTexts(IReadOnlyList<object> values)
    {
        var texts = new List<string>(values.Count);
        foreach (object value in values)
        {
            texts.Add(value.ToString() ?? "");
        }

        return texts;
    }
}
