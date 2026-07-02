using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Radar.Flyouts;
using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Mva;
using Yaat.Sim.Data.Vnas;

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

        var ac = FindMainViewModel()?.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null)
        {
            vm.SelectedAircraft = ac;
        }
    }

    private void OnAircraftCtrlClicked(string callsign)
    {
        var mainVm = FindMainViewModel();
        if (mainVm is null)
        {
            return;
        }

        var ac = mainVm.Aircraft.FirstOrDefault(a => a.Callsign == callsign);
        if (ac is not null)
        {
            FlightPlanEditorManager.Open(ac, mainVm, TopLevel.GetTopLevel(this) as Window);
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

        var ac = FindMainViewModel()?.Aircraft.FirstOrDefault(a => a.Callsign == callsign);

        // Keep the previously-selected aircraft as the command recipient when the
        // controller right-clicks a DIFFERENT aircraft, so selected→right-clicked
        // relative actions (RTIS / FOLLOW) target the selected aircraft. Only adopt
        // the right-clicked aircraft as the selection when nothing was selected or
        // the same aircraft was re-clicked. Left-click remains the way to change selection.
        var prevSelected = vm.SelectedAircraft;
        if (ac is not null && (prevSelected is null || string.Equals(prevSelected.Callsign, callsign, StringComparison.OrdinalIgnoreCase)))
        {
            vm.SelectedAircraft = ac;
        }

        var initials = GetInitials();
        var menu = new ContextMenu();

        var typeText = ac is not null ? $"{callsign} - {ac.DisplayAircraftType}" : callsign;
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
            var routeItem = BuildRouteSummaryItem(ac);
            if (routeItem is not null)
            {
                menu.Items.Add(routeItem);
            }
            var holdItem = BuildHoldStatusItem(ac);
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
                    CommandFlyout.Open(_canvas!, callsign, cmd => vm.SendRawCommandAsync(callsign, initials, cmd));
                    return Task.CompletedTask;
                }
            )
        );
        menu.Items.Add(new Separator());

        menu.Items.Add(FavoritesContextMenu.Build(FindMainViewModel(), ac, callsign, initials));
        menu.Items.Add(new Separator());

        AddRelativeTrafficItems(menu, vm, prevSelected, callsign, initials);

        var profile = ContextMenuProfileService.GetProfile(ac?.CurrentPhase, ac?.IsOnGround ?? false);

        foreach (var group in profile.PrimaryGroups)
        {
            AddMenuGroup(menu, group, vm, callsign, initials, ac);
        }

        if (profile.PrimaryGroups.Count > 0 && profile.SecondaryGroups.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        foreach (var group in profile.SecondaryGroups)
        {
            AddMenuGroup(menu, group, vm, callsign, initials, ac);
        }

        // Always-visible groups
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildTrackSubmenu(vm, callsign, initials));
        menu.Items.Add(BuildDataBlockSubmenu(vm, callsign, initials));
        menu.Items.Add(BuildSquawkSubmenu(vm, callsign, initials));
        menu.Items.Add(BuildAskPilotSubmenu(vm, callsign, initials));
        menu.Items.Add(BuildCoordinationSubmenu(vm, callsign, initials));
        menu.Items.Add(BuildDisplaySubmenu(vm, callsign));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildSimControlSubmenu(vm, callsign, initials, ac));

        // RPO control
        FindMainViewModel()?.BuildRpoMenuItems(menu, [callsign]);

        ShowContextMenu(menu);
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

        var a = selected!.Callsign;
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
        var started = string.IsNullOrEmpty(ac.NavigatingTo);
        foreach (var fix in ac.NavigationRoute)
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
        var displayFixes = fixes.Count > maxDisplay ? string.Join(" ", fixes.Take(maxDisplay)) + " ..." : string.Join(" ", fixes);
        var fullRoute = string.Join(" ", fixes);

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

        var header = ac.HoldKind switch
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

    private MenuItem BuildHeadingSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var hdgLabel = "Heading";
        if (ac is not null)
        {
            if (!string.IsNullOrEmpty(ac.NavigatingTo))
            {
                hdgLabel = $"Heading (\u2192 {ac.NavigatingTo})";
            }
            else if (ac.AssignedHeading.HasValue)
            {
                hdgLabel = $"Heading (\u2192 {ac.AssignedHeading.Value.ToDisplayString()})";
            }
        }

        var menu = new MenuItem { Header = hdgLabel };
        menu.Items.Add(CreateMenuItem("Present heading", () => vm.PresentHeadingAsync(cs, init)));

        var headings = BuildHeadingList();
        var currentHdg = ac is not null ? (int)(Math.Round(ac.Heading.Degrees / 5.0) * 5) : 360;
        if (currentHdg <= 0)
        {
            currentHdg = 360;
        }

        menu.Items.Add(CreateListMenuItem("Fly heading", headings, currentHdg, val => vm.FlyHeadingAsync(cs, init, (int)val)));
        menu.Items.Add(CreateListMenuItem("Turn left", headings, currentHdg, val => vm.TurnLeftAsync(cs, init, (int)val)));
        menu.Items.Add(CreateListMenuItem("Turn right", headings, currentHdg, val => vm.TurnRightAsync(cs, init, (int)val)));

        var relativeDegrees = BuildRelativeTurnList();
        menu.Items.Add(CreateListMenuItem("Turn left (degrees)", relativeDegrees, 30, val => vm.RelativeLeftAsync(cs, init, (int)val)));
        menu.Items.Add(CreateListMenuItem("Turn right (degrees)", relativeDegrees, 30, val => vm.RelativeRightAsync(cs, init, (int)val)));

        return menu;
    }

    private MenuItem BuildAltitudeSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var altLabel = "Altitude";
        if (ac?.AssignedAltitude is not null)
        {
            altLabel = $"Altitude (\u2192 {FormatAltitude((int)ac.AssignedAltitude.Value)})";
        }

        var menu = new MenuItem { Header = altLabel };
        var currentAlt = (int)(ac?.Altitude ?? 0);
        var fieldElev = vm.GetFieldElevation(ac?.Destination);

        var altitudes = BuildFullAltitudeList(fieldElev);
        if (altitudes.Count > 0)
        {
            menu.Items.Add(
                CreateListMenuItem(
                    "Maintain",
                    altitudes,
                    currentAlt,
                    val =>
                    {
                        var selected = (int)val;
                        return selected > currentAlt ? vm.ClimbAndMaintainAsync(cs, init, selected) : vm.DescendAndMaintainAsync(cs, init, selected);
                    },
                    FormatAltitude
                )
            );
        }

        return menu;
    }

    private MenuItem BuildSpeedSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var spdLabel = "Speed";
        if (ac?.AssignedSpeed is not null && ac.AssignedSpeed.Value > 0)
        {
            spdLabel = $"Speed (\u2192 {ac.AssignedSpeed.Value:F0})";
        }

        var menu = new MenuItem { Header = spdLabel };

        var speeds = BuildSpeedListForAircraft(ac);
        var currentSpd =
            ac?.AssignedSpeed is not null && ac.AssignedSpeed.Value > 0
                ? (int)(Math.Round(ac.AssignedSpeed.Value / 10.0) * 10)
                : (int)((IList<object>)speeds)[speeds.Count / 2];
        menu.Items.Add(CreateListMenuItem("Assign speed", speeds, currentSpd, val => vm.SpeedAssignAsync(cs, init, (int)val)));
        menu.Items.Add(CreateInputMenuItem("Speed...", "Speed (knots)", input => vm.SpeedAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Resume normal speed", () => vm.SpeedNormalAsync(cs, init)));
        menu.Items.Add(CreateMenuItem(BuildFasMenuLabel(ac), () => vm.ReduceFinalApproachSpeedAsync(cs, init)));
        return menu;
    }

    private static IReadOnlyList<object> BuildSpeedListForAircraft(AircraftModel? ac)
    {
        if (ac is null || string.IsNullOrEmpty(ac.FiledAircraftType))
        {
            return BuildSpeedList();
        }

        var type = ac.FiledAircraftType;
        var cat = Yaat.Sim.AircraftCategorization.Categorize(type);
        var alt = Math.Max(ac.Altitude, 0);

        double approach = Yaat.Sim.AircraftPerformance.ApproachSpeed(type, cat);
        double climb = Yaat.Sim.AircraftPerformance.ClimbSpeed(type, cat, alt);

        int min = (int)(Math.Floor(approach / 10.0) * 10);
        int max = (int)(Math.Ceiling(climb / 10.0) * 10);

        if (min < 40)
        {
            min = 40;
        }

        if (max - min < 50)
        {
            min = Math.Max(40, min - 20);
            max += 20;
        }

        var items = new List<object>(((max - min) / 10) + 1);
        for (int s = min; s <= max; s += 10)
        {
            items.Add(s);
        }

        return items;
    }

    private static string BuildFasMenuLabel(AircraftModel? ac)
    {
        if (ac is null || string.IsNullOrEmpty(ac.FiledAircraftType))
        {
            return "FAS";
        }

        var category = Yaat.Sim.AircraftCategorization.Categorize(ac.FiledAircraftType);
        var fas = Yaat.Sim.AircraftPerformance.ApproachSpeed(ac.FiledAircraftType, category);
        return fas > 0 ? $"FAS - {fas:F0} kt" : "FAS";
    }

    private MenuItem BuildNavigationSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var navLabel = "Navigation";
        if (ac is not null && !string.IsNullOrEmpty(ac.NavigatingTo))
        {
            navLabel = $"Navigation (\u2192 {ac.NavigatingTo})";
        }

        var menu = new MenuItem { Header = navLabel };

        var routeFixes = ac is not null ? BuildRouteFixList(ac) : [];
        if (vm.FixNames is not null)
        {
            menu.Items.Add(
                CreateFilteredListMenuItem(
                    "Direct to...",
                    vm.FixNames,
                    fix => vm.DirectToAsync(cs, init, fix),
                    routeFixes.Count > 0 ? routeFixes : null
                )
            );
        }
        else if (routeFixes.Count > 0)
        {
            menu.Items.Add(CreateListMenuItem("Direct to", routeFixes, routeFixes[0], val => vm.DirectToAsync(cs, init, (string)val)));
        }
        else
        {
            menu.Items.Add(CreateInputMenuItem("Direct to...", "Fix name", input => vm.DirectToAsync(cs, init, input)));
        }

        bool hasActiveRoute = ac is not null && !string.IsNullOrEmpty(ac.NavigatingTo);
        if (hasActiveRoute)
        {
            if (vm.FixNames is not null)
            {
                menu.Items.Add(
                    CreateFilteredListMenuItem(
                        "Append direct to...",
                        vm.FixNames,
                        fix => vm.AppendDirectToAsync(cs, init, fix),
                        routeFixes.Count > 0 ? routeFixes : null
                    )
                );
            }
            else if (routeFixes.Count > 0)
            {
                menu.Items.Add(
                    CreateListMenuItem("Append direct to", routeFixes, routeFixes[0], val => vm.AppendDirectToAsync(cs, init, (string)val))
                );
            }
            else
            {
                menu.Items.Add(CreateInputMenuItem("Append direct to...", "Fix name", input => vm.AppendDirectToAsync(cs, init, input)));
            }
        }

        return menu;
    }

    private MenuItem BuildHoldSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Hold" };
        menu.Items.Add(CreateMenuItem("Hold present position (left)", () => vm.HoldPresentLeftAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Hold present position (right)", () => vm.HoldPresentRightAsync(cs, init)));

        if (vm.FixNames is not null)
        {
            menu.Items.Add(CreateFilteredListMenuItem("Hold at fix (left)...", vm.FixNames, fix => vm.HoldAtFixLeftAsync(cs, init, fix)));
            menu.Items.Add(CreateFilteredListMenuItem("Hold at fix (right)...", vm.FixNames, fix => vm.HoldAtFixRightAsync(cs, init, fix)));
        }
        else
        {
            menu.Items.Add(CreateInputMenuItem("Hold at fix (left)...", "Fix name", input => vm.HoldAtFixLeftAsync(cs, init, input)));
            menu.Items.Add(CreateInputMenuItem("Hold at fix (right)...", "Fix name", input => vm.HoldAtFixRightAsync(cs, init, input)));
        }

        return menu;
    }

    private MenuItem BuildTrackSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Track" };
        menu.Items.Add(CreateMenuItem("Track", () => vm.TrackAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Drop track", () => vm.DropTrackAsync(cs, init)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Accept handoff", () => vm.AcceptHandoffAsync(cs, init)));
        menu.Items.Add(CreateInputMenuItem("Initiate handoff...", "Position ID", input => vm.InitiateHandoffAsync(cs, init, input)));
        menu.Items.Add(CreateMenuItem("Cancel handoff", () => vm.CancelHandoffAsync(cs, init)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateInputMenuItem("Point out...", "Position ID", input => vm.PointOutAsync(cs, init, input)));
        menu.Items.Add(CreateMenuItem("Acknowledge pointout", () => vm.AcknowledgeAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildDataBlockSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Data Block" };
        menu.Items.Add(CreateInputMenuItem("Scratchpad...", "Text", input => vm.ScratchpadAsync(cs, init, input)));
        menu.Items.Add(CreateInputMenuItem("Note...", "Note text (max 40)", input => vm.NoteAsync(cs, init, input)));
        menu.Items.Add(CreateInputMenuItem("Temporary altitude...", "Altitude", input => vm.TemporaryAltitudeAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateInputMenuItem("Cruise...", "Altitude", input => vm.CruiseAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Annotate", () => vm.AnnotateAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildSquawkSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Squawk" };
        menu.Items.Add(CreateInputMenuItem("Squawk...", "Code (0000-7777)", input => vm.SquawkAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Squawk random", () => vm.RandomSquawkAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Squawk VFR", () => vm.SquawkVfrAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Squawk normal", () => vm.SquawkNormalAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Squawk standby", () => vm.SquawkStandbyAsync(cs, init)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Ident", () => vm.IdentAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildAskPilotSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Ask pilot to say..." };
        menu.Items.Add(CreateMenuItem("Altitude", () => vm.SayAltitudeAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Heading", () => vm.SayHeadingAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Speed", () => vm.SaySpeedAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Mach", () => vm.SayMachAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Position", () => vm.SayPositionAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Expected approach", () => vm.SayExpectedApproachAsync(cs, init)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateInputMenuItem("Custom...", "Text", input => vm.SayCustomAsync(cs, init, input)));
        return menu;
    }

    private static MenuItem BuildCoordinationSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Coordination" };
        menu.Items.Add(CreateMenuItem("Release", () => vm.CoordinationReleaseAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Hold", () => vm.CoordinationHoldAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Recall", () => vm.CoordinationRecallAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Acknowledge release", () => vm.CoordinationAcknowledgeAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildSimControlSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Sim Control" };
        var warpItem = new MenuItem { Header = "Warp..." };
        warpItem.Click += (_, _) =>
        {
            var hdg = ac is not null ? (int)Math.Round(ac.Heading.Degrees) : 0;
            if (hdg <= 0)
            {
                hdg = 360;
            }

            var alt = ac is not null ? (int)Math.Round(ac.Altitude) : 0;
            var spd = ac is not null ? (int)Math.Round(ac.IndicatedAirspeed) : 0;
            Dispatcher.UIThread.Post(() => ShowWarpPopup(cs, "", hdg, alt, spd, (frd, h, a, s) => _ = vm.WarpAsync(cs, init, frd, h, a, s)));
        };
        menu.Items.Add(warpItem);
        menu.Items.Add(CreateMenuItem("Delete", () => vm.DeleteAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildDisplaySubmenu(RadarViewModel vm, string callsign)
    {
        var menu = new MenuItem { Header = "Display" };
        var isMinified = _canvas?.IsMinified(callsign) ?? false;
        menu.Items.Add(
            CreateMenuItem(
                isMinified ? "Full datablock" : "Mini datablock",
                () =>
                {
                    _canvas?.ToggleMinifiedDataBlock(callsign);
                    return Task.CompletedTask;
                }
            )
        );
        if (_canvas?.HasManualDataBlockOffset(callsign) ?? false)
        {
            menu.Items.Add(
                CreateMenuItem(
                    "Reset to student position",
                    () =>
                    {
                        _canvas?.ResetDataBlockOffset(callsign);
                        return Task.CompletedTask;
                    }
                )
            );
        }
        var isPathShown = vm.IsPathShown(callsign);
        menu.Items.Add(
            new MenuItem
            {
                Header = isPathShown ? "Hide flight path" : "Show flight path",
                Command = new RelayCommand(() => vm.ToggleShowPath(callsign)),
            }
        );
        menu.Items.Add(new Separator());

        var ldr = new MenuItem { Header = "Leader direction" };
        for (int d = 1; d <= 9; d++)
        {
            var direction = d;
            var label = direction == 5 ? "5 (default)" : direction.ToString();
            ldr.Items.Add(CreateMenuItem(label, () => vm.LeaderDirectionAsync(callsign, GetInitials(), direction)));
        }

        menu.Items.Add(ldr);

        var jring = new MenuItem { Header = "J-ring" };
        jring.Items.Add(CreateMenuItem("Clear", () => vm.JRingAsync(callsign, GetInitials(), null)));
        foreach (var r in new[] { 1.0, 2.0, 3.0, 5.0, 10.0 })
        {
            var radius = r;
            jring.Items.Add(CreateMenuItem($"{radius:0} nm", () => vm.JRingAsync(callsign, GetInitials(), radius)));
        }

        menu.Items.Add(jring);

        var cone = new MenuItem { Header = "Cone" };
        cone.Items.Add(CreateMenuItem("Clear", () => vm.ConeAsync(callsign, GetInitials(), null)));
        foreach (var l in new[] { 1.0, 2.0, 3.0, 5.0, 10.0 })
        {
            var length = l;
            cone.Items.Add(CreateMenuItem($"{length:0} nm", () => vm.ConeAsync(callsign, GetInitials(), length)));
        }

        menu.Items.Add(cone);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Blank target", () => vm.BlankAsync(callsign, GetInitials())));
        menu.Items.Add(CreateMenuItem("Unblank target", () => vm.BlankDeleteAsync(callsign, GetInitials())));
        return menu;
    }

    private MenuItem BuildApproachSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var apchLabel = "Approach";
        if (ac is not null)
        {
            if (!string.IsNullOrEmpty(ac.ActiveApproachId))
            {
                apchLabel = $"Approach ({ac.ActiveApproachId})";
            }
            else if (!string.IsNullOrEmpty(ac.ExpectedApproach))
            {
                apchLabel = $"Approach (exp: {ac.ExpectedApproach})";
            }
        }

        var menu = new MenuItem { Header = apchLabel };

        IReadOnlyList<CifpApproachProcedure>? approaches = null;
        if (ac is not null && !string.IsNullOrEmpty(ac.Destination))
        {
            var fromDb = NavigationDatabase.Instance.GetApproaches(ac.Destination);
            if (fromDb.Count > 0)
            {
                approaches = fromDb;
            }
        }

        if (approaches is not null)
        {
            var ids = approaches.Select(a => (object)a.ApproachId).ToList();
            menu.Items.Add(CreateListMenuItem("Cleared approach", ids, ids[0], val => vm.ClearedApproachAsync(cs, init, (string)val)));
            menu.Items.Add(CreateListMenuItem("Join approach", ids, ids[0], val => vm.JoinApproachAsync(cs, init, (string)val)));
            menu.Items.Add(CreateListMenuItem("Cleared straight-in", ids, ids[0], val => vm.ClearedApproachStraightInAsync(cs, init, (string)val)));
            menu.Items.Add(CreateListMenuItem("Join straight-in", ids, ids[0], val => vm.JoinApproachStraightInAsync(cs, init, (string)val)));
            menu.Items.Add(CreateListMenuItem("Cleared approach (force)", ids, ids[0], val => vm.ClearedApproachForceAsync(cs, init, (string)val)));
            menu.Items.Add(CreateListMenuItem("Join approach (force)", ids, ids[0], val => vm.JoinApproachForceAsync(cs, init, (string)val)));
            menu.Items.Add(
                CreateListMenuItem("Join final approach course", ids, ids[0], val => vm.JoinFinalApproachCourseAsync(cs, init, (string)val))
            );
            menu.Items.Add(CreateListMenuItem("Expect approach", ids, ids[0], val => vm.ExpectApproachAsync(cs, init, (string)val)));
        }
        else
        {
            menu.Items.Add(CreateInputMenuItem("Cleared approach...", "Approach ID", input => vm.ClearedApproachAsync(cs, init, input)));
            menu.Items.Add(CreateInputMenuItem("Join approach...", "Approach ID", input => vm.JoinApproachAsync(cs, init, input)));
            menu.Items.Add(CreateInputMenuItem("Cleared straight-in...", "Approach ID", input => vm.ClearedApproachStraightInAsync(cs, init, input)));
            menu.Items.Add(CreateInputMenuItem("Join straight-in...", "Approach ID", input => vm.JoinApproachStraightInAsync(cs, init, input)));
            menu.Items.Add(CreateInputMenuItem("Cleared approach (force)...", "Approach ID", input => vm.ClearedApproachForceAsync(cs, init, input)));
            menu.Items.Add(CreateInputMenuItem("Join approach (force)...", "Approach ID", input => vm.JoinApproachForceAsync(cs, init, input)));
            menu.Items.Add(
                CreateInputMenuItem("Join final approach course...", "Approach ID", input => vm.JoinFinalApproachCourseAsync(cs, init, input))
            );
            menu.Items.Add(CreateInputMenuItem("Expect approach...", "Approach ID", input => vm.ExpectApproachAsync(cs, init, input)));
        }

        AddVisualApproachItems(menu, vm, cs, init, ac, approaches);

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Report field in sight", () => vm.ReportFieldInSightAsync(cs, init)));
        menu.Items.Add(
            CreateInputMenuItem("Report traffic in sight...", "Target callsign (optional)", input => vm.ReportTrafficInSightAsync(cs, init, input))
        );

        menu.Items.Add(new Separator());
        menu.Items.Add(BuildReportWhenSubmenu(vm, cs, init));

        return menu;
    }

    private MenuItem BuildReportWhenSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Report when…" };
        menu.Items.Add(CreateMenuItem("Turning base", () => vm.SendRawCommandAsync(cs, init, "REPORT BASE")));
        menu.Items.Add(CreateMenuItem("Turning final", () => vm.SendRawCommandAsync(cs, init, "REPORT FINAL")));
        menu.Items.Add(CreateMenuItem("Turning crosswind", () => vm.SendRawCommandAsync(cs, init, "REPORT CROSSWIND")));
        menu.Items.Add(CreateMenuItem("Turning downwind", () => vm.SendRawCommandAsync(cs, init, "REPORT DOWNWIND")));
        menu.Items.Add(CreateInputMenuItem("N-mile final...", "Distance (NM)", input => vm.SendRawCommandAsync(cs, init, $"REPORT {input} FINAL")));
        menu.Items.Add(CreateInputMenuItem("At fix...", "Fix name", input => vm.SendRawCommandAsync(cs, init, $"REPORT {input}")));

        var stop = new MenuItem { Header = "Stop reporting" };
        stop.Items.Add(CreateMenuItem("Base", () => vm.SendRawCommandAsync(cs, init, "REPORT OFF BASE")));
        stop.Items.Add(CreateMenuItem("Final", () => vm.SendRawCommandAsync(cs, init, "REPORT OFF FINAL")));
        stop.Items.Add(CreateMenuItem("Crosswind", () => vm.SendRawCommandAsync(cs, init, "REPORT OFF CROSSWIND")));
        stop.Items.Add(CreateMenuItem("Downwind", () => vm.SendRawCommandAsync(cs, init, "REPORT OFF DOWNWIND")));
        stop.Items.Add(new Separator());
        stop.Items.Add(CreateMenuItem("All reports", () => vm.SendRawCommandAsync(cs, init, "REPORT OFF")));

        menu.Items.Add(new Separator());
        menu.Items.Add(stop);
        return menu;
    }

    private void AddVisualApproachItems(
        MenuItem menu,
        RadarViewModel vm,
        string cs,
        string init,
        AircraftModel? ac,
        IReadOnlyList<CifpApproachProcedure>? approaches
    )
    {
        var defaultRunway = TryGetSmartRunway(ac, approaches);
        var runways = ac is not null && !string.IsNullOrEmpty(ac.Destination) ? GetRunwayDesignators(ac.Destination) : [];

        if (defaultRunway is not null)
        {
            menu.Items.Add(
                CreateMenuItem(
                    $"Cleared visual approach {RunwayIdentifier.ToDisplayDesignator(defaultRunway)}",
                    () => vm.ClearedVisualApproachAsync(cs, init, defaultRunway)
                )
            );
        }

        if (runways.Count > 0)
        {
            var label = defaultRunway is not null ? "Cleared visual approach (other)..." : "Cleared visual approach...";
            var items = runways.Cast<object>().ToList();
            menu.Items.Add(CreateListMenuItem(label, items, items[0], val => vm.ClearedVisualApproachAsync(cs, init, (string)val)));
        }
        else if (defaultRunway is null)
        {
            menu.Items.Add(
                CreateInputMenuItem("Cleared visual approach...", "Runway (e.g. 28R)", input => vm.ClearedVisualApproachAsync(cs, init, input))
            );
        }
    }

    private static string? TryGetSmartRunway(AircraftModel? ac, IReadOnlyList<CifpApproachProcedure>? approaches)
    {
        if (ac is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(ac.AssignedRunway))
        {
            return ac.AssignedRunway;
        }

        if (approaches is null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(ac.ActiveApproachId))
        {
            var rwy = approaches.FirstOrDefault(a => string.Equals(a.ApproachId, ac.ActiveApproachId, StringComparison.OrdinalIgnoreCase))?.Runway;
            if (!string.IsNullOrEmpty(rwy))
            {
                return rwy;
            }
        }

        if (!string.IsNullOrEmpty(ac.ExpectedApproach))
        {
            var rwy = approaches.FirstOrDefault(a => string.Equals(a.ApproachId, ac.ExpectedApproach, StringComparison.OrdinalIgnoreCase))?.Runway;
            if (!string.IsNullOrEmpty(rwy))
            {
                return rwy;
            }
        }

        return null;
    }

    private void AddJoinStarItems(MenuItem menu, RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var defaultStar = TryGetFiledStar(ac);
        var starIds = ac is not null && !string.IsNullOrEmpty(ac.Destination) ? GetStarIds(ac.Destination) : [];

        if (defaultStar is not null)
        {
            menu.Items.Add(CreateMenuItem($"Join STAR {defaultStar}", () => vm.JoinStarAsync(cs, init, defaultStar)));
        }

        if (starIds.Count > 0)
        {
            var label = defaultStar is not null ? "Join STAR (other)..." : "Join STAR...";
            var items = starIds.Cast<object>().ToList();
            menu.Items.Add(CreateListMenuItem(label, items, items[0], val => vm.JoinStarAsync(cs, init, (string)val)));
        }
        else if (defaultStar is null)
        {
            menu.Items.Add(CreateInputMenuItem("Join STAR...", "STAR name", input => vm.JoinStarAsync(cs, init, input)));
        }
    }

    private static string? TryGetFiledStar(AircraftModel? ac)
    {
        if (ac is null || string.IsNullOrEmpty(ac.Destination) || string.IsNullOrEmpty(ac.Route))
        {
            return null;
        }

        var tokens = ac.Route.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var star = NavigationDatabase.Instance.GetStar(ac.Destination, trimmed);
            if (star is not null)
            {
                return star.ProcedureId;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetStarIds(string airportCode)
    {
        var stars = NavigationDatabase.Instance.GetStars(airportCode);
        if (stars.Count == 0)
        {
            return [];
        }

        var ids = new List<string>(stars.Count);
        foreach (var s in stars)
        {
            ids.Add(s.ProcedureId);
        }

        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    private static IReadOnlyList<string> GetRunwayDesignators(string airportCode)
    {
        var runways = NavigationDatabase.Instance.GetRunways(airportCode);
        if (runways.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var rwy in runways)
        {
            // De-pad for the picker labels (FAA form). The selected value is re-normalized
            // server-side, so the command still resolves the runway. Dedup on the canonical
            // end so both representations of a runway collapse to one entry.
            if (!string.IsNullOrEmpty(rwy.Id.End1) && seen.Add(rwy.Id.End1))
            {
                result.Add(RunwayIdentifier.ToDisplayDesignator(rwy.Id.End1));
            }

            if (!string.IsNullOrEmpty(rwy.Id.End2) && seen.Add(rwy.Id.End2))
            {
                result.Add(RunwayIdentifier.ToDisplayDesignator(rwy.Id.End2));
            }
        }

        result.Sort(RunwayDesignatorComparer.Instance);
        return result;
    }

    /// <summary>
    /// Returns airway IDs found in the aircraft's filed route, in filed order.
    /// We never offer "all airways" — global CIFP exposes thousands and the picker
    /// would be useless.
    /// </summary>
    private static IReadOnlyList<string> GetFiledAirways(AircraftModel? ac)
    {
        if (ac is null || string.IsNullOrEmpty(ac.Route))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var token in ac.Route.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (NavigationDatabase.Instance.IsAirway(trimmed) && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    private void AddJoinAirwayItems(MenuItem menu, RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var filed = GetFiledAirways(ac);

        if (filed.Count == 1)
        {
            var only = filed[0];
            menu.Items.Add(CreateMenuItem($"Join airway {only}", () => vm.JoinAirwayAsync(cs, init, only)));
            menu.Items.Add(CreateInputMenuItem("Join airway (other)...", "Airway ID", input => vm.JoinAirwayAsync(cs, init, input)));
            return;
        }

        if (filed.Count > 1)
        {
            var items = filed.Cast<object>().ToList();
            menu.Items.Add(CreateListMenuItem("Join airway...", items, items[0], val => vm.JoinAirwayAsync(cs, init, (string)val)));
            menu.Items.Add(CreateInputMenuItem("Join airway (other)...", "Airway ID", input => vm.JoinAirwayAsync(cs, init, input)));
            return;
        }

        menu.Items.Add(CreateInputMenuItem("Join airway...", "Airway ID", input => vm.JoinAirwayAsync(cs, init, input)));
    }

    /// <summary>
    /// Returns suggested fix names for this aircraft, in the canonical order shared with the
    /// typed autocomplete: active navigation route, then filed-route fixes, then destination,
    /// then departure. Deduped. We never offer "all fixes" — global CIFP has tens of thousands.
    /// </summary>
    private static IReadOnlyList<string> GetRouteFixes(AircraftModel? ac) => ac is null ? [] : FixSuggester.CollectRouteFixNames(ac);

    private void AddRouteFixItem(MenuItem menu, string label, AircraftModel? ac, Func<string, Task> dispatch)
    {
        var fixes = GetRouteFixes(ac);

        if (fixes.Count == 1)
        {
            var only = fixes[0];
            menu.Items.Add(CreateMenuItem($"{label} {only}", () => dispatch(only)));
            menu.Items.Add(CreateInputMenuItem($"{label} (other)...", "Fix name", input => dispatch(input)));
            return;
        }

        if (fixes.Count > 1)
        {
            var items = fixes.Cast<object>().ToList();
            menu.Items.Add(CreateListMenuItem($"{label}...", items, items[0], val => dispatch((string)val)));
            menu.Items.Add(CreateInputMenuItem($"{label} (other)...", "Fix name", input => dispatch(input)));
            return;
        }

        menu.Items.Add(CreateInputMenuItem($"{label}...", "Fix name", input => dispatch(input)));
    }

    private MenuItem BuildProceduresSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Procedures" };
        AddJoinStarItems(menu, vm, cs, init, ac);
        menu.Items.Add(CreateMenuItem("Climb via SID", () => vm.ClimbViaSidAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Descend via STAR", () => vm.DescendViaStarAsync(cs, init)));

        AddRouteFixItem(menu, "Cross fix", ac, val => vm.CrossFixAsync(cs, init, val));
        AddRouteFixItem(menu, "Depart fix", ac, val => vm.DepartFixAsync(cs, init, val));

        menu.Items.Add(CreateInputMenuItem("PTAC...", "PTAC arguments", input => vm.PtacAsync(cs, init, input)));

        AddJoinAirwayItems(menu, vm, cs, init, ac);

        AddJoinRadialItems(menu, vm, cs, init);

        return menu;
    }

    private void AddJoinRadialItems(MenuItem menu, RadarViewModel vm, string cs, string init)
    {
        if (vm.FixNames is not null)
        {
            menu.Items.Add(
                CreateFilteredListMenuItem(
                    "Join radial outbound...",
                    vm.FixNames,
                    fix =>
                    {
                        Dispatcher.UIThread.Post(() =>
                            ShowInputPopup($"Bearing from {fix} (0-360)", bearing => vm.JoinRadialOutboundAsync(cs, init, $"{fix} {bearing}"))
                        );
                        return Task.CompletedTask;
                    }
                )
            );
            menu.Items.Add(
                CreateFilteredListMenuItem(
                    "Join radial inbound...",
                    vm.FixNames,
                    fix =>
                    {
                        Dispatcher.UIThread.Post(() =>
                            ShowInputPopup($"Bearing to {fix} (0-360)", bearing => vm.JoinRadialInboundAsync(cs, init, $"{fix} {bearing}"))
                        );
                        return Task.CompletedTask;
                    }
                )
            );
        }
        else
        {
            menu.Items.Add(CreateInputMenuItem("Join radial outbound...", "FIX bearing", input => vm.JoinRadialOutboundAsync(cs, init, input)));
            menu.Items.Add(CreateInputMenuItem("Join radial inbound...", "FIX bearing", input => vm.JoinRadialInboundAsync(cs, init, input)));
        }
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
        var rwy = !string.IsNullOrEmpty(ac?.AssignedRunway) ? $" {RunwayIdentifier.ToDisplayDesignator(ac.AssignedRunway)}" : "";

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
        var canLand = AircraftCommandApplicability.CanClearToLand(ac);
        var canGoAround = AircraftCommandApplicability.CanGoAround(ac);
        var canCancelLanding = AircraftCommandApplicability.CanCancelLandingClearance(ac);
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
                if (AircraftCommandApplicability.CanIssueVfrOption(ac))
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

        // On-course, pattern, and closed-traffic modifiers are VFR-only — the server rejects them for IFR.
        if (AircraftCommandApplicability.ShowVfrTakeoffModifiers(ac))
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

    private void AddMenuGroup(ContextMenu menu, MenuGroup group, RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        switch (group)
        {
            case MenuGroup.Heading:
                menu.Items.Add(BuildHeadingSubmenu(vm, cs, init, ac));
                break;
            case MenuGroup.Altitude:
                menu.Items.Add(BuildAltitudeSubmenu(vm, cs, init, ac));
                break;
            case MenuGroup.Speed:
                menu.Items.Add(BuildSpeedSubmenu(vm, cs, init, ac));
                break;
            case MenuGroup.Navigation:
                menu.Items.Add(BuildNavigationSubmenu(vm, cs, init, ac));
                break;
            case MenuGroup.DrawRoute:
                menu.Items.Add(
                    CreateMenuItem(
                        "Draw route",
                        () =>
                        {
                            vm.EnterDrawRoute(cs);
                            return Task.CompletedTask;
                        }
                    )
                );
                break;
            case MenuGroup.Hold:
                menu.Items.Add(BuildHoldSubmenu(vm, cs, init));
                break;
            case MenuGroup.Approach:
                menu.Items.Add(BuildApproachSubmenu(vm, cs, init, ac));
                break;
            case MenuGroup.Procedures:
                menu.Items.Add(BuildProceduresSubmenu(vm, cs, init, ac));
                break;
            case MenuGroup.Tower:
                var tower = BuildTowerSubmenu(vm, cs, init, ac);
                if (tower is not null)
                {
                    menu.Items.Add(tower);
                }
                break;
            case MenuGroup.Pattern:
                var pattern = BuildPatternSubmenu(vm, cs, init, ac);
                if (pattern is not null)
                {
                    menu.Items.Add(pattern);
                }
                break;
        }
    }

    /// <summary>
    /// Builds the Pattern submenu. Entries are offered to airborne VFR aircraft being
    /// sequenced in; maneuvers are leg-specific (turn-crosswind only from upwind, etc.).
    /// Pattern operations are VFR-only. Returns null when nothing applies.
    /// </summary>
    internal MenuItem? BuildPatternSubmenu(RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var menu = new MenuItem { Header = "Pattern" };
        if (AircraftCommandApplicability.CanEnterPattern(ac))
        {
            AddPatternEntryItems(menu, vm, cs, init, ac);
        }
        AddPatternManeuverItems(menu, vm, cs, init, ac);
        return menu.Items.Count > 0 ? menu : null;
    }

    private void AddPatternManeuverItems(MenuItem menu, RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        // Pattern maneuvers are VFR-only and valid only from specific legs of the circuit.
        if (!AircraftCommandApplicability.IsVfr(ac))
        {
            return;
        }

        var phase = ac?.CurrentPhase ?? "";

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
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }
    }

    private void AddPatternEntryItems(MenuItem menu, RadarViewModel vm, string cs, string init, AircraftModel? ac)
    {
        var runwayAirport = ac is not null ? (!string.IsNullOrEmpty(ac.Destination) ? ac.Destination : ac.Departure) : null;
        var runways = !string.IsNullOrEmpty(runwayAirport) ? GetRunwayDesignators(runwayAirport) : [];
        var defaultRunway = !string.IsNullOrEmpty(ac?.AssignedRunway) ? ac.AssignedRunway : null;

        AddPatternEntry(menu, "Enter left downwind", runways, defaultRunway, rwy => vm.EnterLeftDownwindAsync(cs, init, rwy));
        AddPatternEntry(menu, "Enter right downwind", runways, defaultRunway, rwy => vm.EnterRightDownwindAsync(cs, init, rwy));
        AddPatternEntry(menu, "Enter left base", runways, defaultRunway, rwy => vm.EnterLeftBaseAsync(cs, init, rwy));
        AddPatternEntry(menu, "Enter right base", runways, defaultRunway, rwy => vm.EnterRightBaseAsync(cs, init, rwy));
        AddPatternEntry(menu, "Enter straight-in final", runways, defaultRunway, rwy => vm.EnterFinalAsync(cs, init, rwy));
    }

    private void AddPatternEntry(MenuItem menu, string baseLabel, IReadOnlyList<string> runways, string? defaultRunway, Func<string?, Task> action)
    {
        if (defaultRunway is not null)
        {
            menu.Items.Add(CreateMenuItem($"{baseLabel} {RunwayIdentifier.ToDisplayDesignator(defaultRunway)}", () => action(defaultRunway)));
        }

        if (runways.Count > 0)
        {
            var label = defaultRunway is not null ? $"{baseLabel} (other)..." : $"{baseLabel}...";
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
            var frd = frdString;
            menu.Items.Add(
                CreateMenuItem(
                    "Copy FRD",
                    async () =>
                    {
                        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                        if (clipboard is not null)
                        {
                            await clipboard.SetTextAsync(frd);
                        }
                    }
                )
            );

            // Scope-marker pins (CRC ".ff"/".marker"). Pick radius scales with the current range.
            var pickNm = Math.Max(0.5, vm.RangeNm * 0.04);
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

        // MVA at the clicked point (FAA-charted; only the loaded facility's coverage, null elsewhere).
        var mvaSector = MvaDatabase.Default.FindSector(new LatLon(lat, lon));
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
            var callsign = vm.SelectedAircraft.Callsign;
            var initials = GetInitials();

            var heading = (int)(Math.Round(GeoMath.BearingTo(vm.SelectedAircraft.Position, new LatLon(lat, lon)) / 5.0) * 5);
            if (heading <= 0)
            {
                heading = 360;
            }

            menu.Items.Add(
                CreateMenuItem($"Fly heading {new MagneticHeading(heading).ToDisplayString()}", () => vm.FlyHeadingAsync(callsign, initials, heading))
            );

            if (frdString is not null)
            {
                var target = frdString;
                menu.Items.Add(CreateMenuItem($"Direct to {target}", () => vm.DirectToAsync(callsign, initials, target)));
                menu.Items.Add(CreateMenuItem($"Append direct to {target}", () => vm.AppendDirectToAsync(callsign, initials, target)));
                menu.Items.Add(CreateMenuItem($"Hold at {target} (left)", () => vm.HoldAtFixLeftAsync(callsign, initials, target)));
                menu.Items.Add(CreateMenuItem($"Hold at {target} (right)", () => vm.HoldAtFixRightAsync(callsign, initials, target)));

                var warpFrd = target;
                var warpHdg = (int)Math.Round(vm.SelectedAircraft.Heading.Degrees);
                if (warpHdg <= 0)
                {
                    warpHdg = 360;
                }

                var warpAlt = (int)Math.Round(vm.SelectedAircraft.Altitude);
                var warpSpd = (int)Math.Round(vm.SelectedAircraft.IndicatedAirspeed);
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

    private MenuItem CreateInputMenuItem(string header, string placeholder, Func<string, Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => ShowInputPopup(placeholder, action));
        };
        return item;
    }

    private MenuItem CreateFilteredListMenuItem(
        string header,
        string[] sortedNames,
        Func<string, Task> action,
        IReadOnlyList<object>? priorityItems = null
    )
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            Dispatcher.UIThread.Post(() => ShowFilteredListPopup(sortedNames, action, priorityItems));
        };
        return item;
    }

    private MenuItem CreateListMenuItem(
        string header,
        IReadOnlyList<object> items,
        object? selectedValue,
        Func<object, Task> action,
        Func<int, string>? formatLabel = null
    )
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (formatLabel is not null)
                {
                    var labeled = new List<object>(items.Count);
                    foreach (var i in items)
                    {
                        labeled.Add(new LabeledValue(formatLabel((int)i), (int)i));
                    }

                    ShowListPopup(
                        labeled,
                        null,
                        val =>
                        {
                            var lv = (LabeledValue)val;
                            return action(lv.Value);
                        }
                    );
                }
                else
                {
                    ShowListPopup(items, selectedValue, action);
                }
            });
        };
        return item;
    }
}
