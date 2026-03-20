using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data;

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
        if (ac is not null)
        {
            vm.SelectedAircraft = ac;
        }

        var initials = GetInitials();
        var menu = new ContextMenu();

        var typeText = ac is not null ? $"{callsign} - {ac.AircraftType}" : callsign;
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
        }

        menu.Items.Add(new Separator());
        AddCommandTextBox(menu, cmd => vm.SendRawCommandAsync(callsign, initials, cmd));
        menu.Items.Add(new Separator());

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
        menu.Items.Add(BuildCoordinationSubmenu(vm, callsign, initials));
        menu.Items.Add(BuildDisplaySubmenu(vm, callsign));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildSimControlSubmenu(vm, callsign, initials, ac));

        // RPO control
        FindMainViewModel()?.BuildRpoMenuItems(menu, [callsign]);

        ShowContextMenu(menu);
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
                hdgLabel = $"Heading (\u2192 {ac.AssignedHeading.Value:F0})";
            }
        }

        var menu = new MenuItem { Header = hdgLabel };
        menu.Items.Add(CreateMenuItem("Present heading", () => vm.PresentHeadingAsync(cs, init)));

        var headings = BuildHeadingList();
        var currentHdg = ac is not null ? (int)(Math.Round(ac.Heading / 5.0) * 5) : 360;
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

        var speeds = BuildSpeedList();
        var currentSpd = ac?.AssignedSpeed is not null && ac.AssignedSpeed.Value > 0 ? (int)(Math.Round(ac.AssignedSpeed.Value / 10.0) * 10) : 250;
        menu.Items.Add(CreateListMenuItem("Assign speed", speeds, currentSpd, val => vm.SpeedAssignAsync(cs, init, (int)val)));
        menu.Items.Add(CreateInputMenuItem("Speed...", "Speed (knots)", input => vm.SpeedAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Resume normal speed", () => vm.SpeedNormalAsync(cs, init)));
        return menu;
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
        menu.Items.Add(CreateInputMenuItem("Temporary altitude...", "Altitude", input => vm.TemporaryAltitudeAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateInputMenuItem("Cruise...", "Altitude", input => vm.CruiseAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Annotate", () => vm.AnnotateAsync(cs, init)));
        return menu;
    }

    private MenuItem BuildSquawkSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Squawk" };
        menu.Items.Add(CreateInputMenuItem("Squawk...", "Code (0000-7777)", input => vm.SquawkAsync(cs, init, int.Parse(input))));
        menu.Items.Add(CreateMenuItem("Squawk VFR", () => vm.SquawkVfrAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Squawk normal", () => vm.SquawkNormalAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Squawk standby", () => vm.SquawkStandbyAsync(cs, init)));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Ident", () => vm.IdentAsync(cs, init)));
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
            var hdg = ac is not null ? (int)Math.Round(ac.Heading) : 0;
            if (hdg <= 0)
            {
                hdg = 360;
            }

            var alt = ac is not null ? (int)Math.Round(ac.Altitude) : 0;
            var spd = ac is not null ? (int)Math.Round(ac.IndicatedAirspeed) : 0;
            ShowWarpPopup(cs, "", hdg, alt, spd, (frd, h, a, s) => _ = vm.WarpAsync(cs, init, frd, h, a, s));
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
        var isPathShown = vm.IsPathShown(callsign);
        menu.Items.Add(
            new MenuItem
            {
                Header = isPathShown ? "Hide flight path" : "Show flight path",
                Command = new RelayCommand(() => vm.ToggleShowPath(callsign)),
            }
        );
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

        // Try to get approach list from CIFP data
        IReadOnlyList<string>? approachIds = null;
        if (ac is not null && !string.IsNullOrEmpty(ac.Destination))
        {
            var approaches = NavigationDatabase.Instance.GetApproaches(ac.Destination);
            if (approaches.Count > 0)
            {
                approachIds = approaches.Select(a => a.ApproachId).ToList();
            }
        }

        if (approachIds is not null)
        {
            var ids = approachIds.Cast<object>().ToList();
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

        menu.Items.Add(
            CreateInputMenuItem("Cleared visual approach...", "Runway (e.g. 28R)", input => vm.ClearedVisualApproachAsync(cs, init, input))
        );

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Report field in sight", () => vm.ReportFieldInSightAsync(cs, init)));
        menu.Items.Add(
            CreateInputMenuItem("Report traffic in sight...", "Target callsign (optional)", input => vm.ReportTrafficInSightAsync(cs, init, input))
        );

        return menu;
    }

    private MenuItem BuildProceduresSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Procedures" };
        menu.Items.Add(CreateInputMenuItem("Join STAR...", "STAR name", input => vm.JoinStarAsync(cs, init, input)));
        menu.Items.Add(CreateMenuItem("Climb via SID", () => vm.ClimbViaSidAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Descend via STAR", () => vm.DescendViaStarAsync(cs, init)));

        if (vm.FixNames is not null)
        {
            menu.Items.Add(CreateFilteredListMenuItem("Cross fix...", vm.FixNames, fix => vm.CrossFixAsync(cs, init, fix)));
            menu.Items.Add(CreateFilteredListMenuItem("Depart fix...", vm.FixNames, fix => vm.DepartFixAsync(cs, init, fix)));
        }
        else
        {
            menu.Items.Add(CreateInputMenuItem("Cross fix...", "Fix name", input => vm.CrossFixAsync(cs, init, input)));
            menu.Items.Add(CreateInputMenuItem("Depart fix...", "Fix name", input => vm.DepartFixAsync(cs, init, input)));
        }

        menu.Items.Add(CreateInputMenuItem("PTAC...", "PTAC arguments", input => vm.PtacAsync(cs, init, input)));

        return menu;
    }

    private MenuItem BuildTowerSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Tower" };
        menu.Items.Add(CreateMenuItem("Cleared to land", () => vm.ClearedToLandAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Cleared for the option", () => vm.ClearedForOptionAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Touch and go", () => vm.TouchAndGoAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Stop and go", () => vm.StopAndGoAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Low approach", () => vm.LowApproachAsync(cs, init)));
        menu.Items.Add(CreateMenuItem("Go around", () => vm.GoAroundAsync(cs, init)));
        menu.Items.Add(new Separator());
        menu.Items.Add(
            CreateInputMenuItem("Enter left downwind...", "Runway (optional)", input => vm.EnterLeftDownwindAsync(cs, init, NullIfEmpty(input)))
        );
        menu.Items.Add(
            CreateInputMenuItem("Enter right downwind...", "Runway (optional)", input => vm.EnterRightDownwindAsync(cs, init, NullIfEmpty(input)))
        );
        menu.Items.Add(CreateInputMenuItem("Enter left base...", "Runway (optional)", input => vm.EnterLeftBaseAsync(cs, init, NullIfEmpty(input))));
        menu.Items.Add(
            CreateInputMenuItem("Enter right base...", "Runway (optional)", input => vm.EnterRightBaseAsync(cs, init, NullIfEmpty(input)))
        );
        menu.Items.Add(
            CreateInputMenuItem("Enter straight-in final...", "Runway (optional)", input => vm.EnterFinalAsync(cs, init, NullIfEmpty(input)))
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
                menu.Items.Add(BuildProceduresSubmenu(vm, cs, init));
                break;
            case MenuGroup.Tower:
                menu.Items.Add(BuildTowerSubmenu(vm, cs, init));
                break;
            case MenuGroup.Pattern:
                menu.Items.Add(BuildPatternSubmenu(vm, cs, init));
                break;
        }
    }

    private MenuItem BuildPatternSubmenu(RadarViewModel vm, string cs, string init)
    {
        var menu = new MenuItem { Header = "Pattern" };
        menu.Items.Add(
            CreateInputMenuItem("Enter left downwind...", "Runway (optional)", input => vm.EnterLeftDownwindAsync(cs, init, NullIfEmpty(input)))
        );
        menu.Items.Add(
            CreateInputMenuItem("Enter right downwind...", "Runway (optional)", input => vm.EnterRightDownwindAsync(cs, init, NullIfEmpty(input)))
        );
        menu.Items.Add(CreateInputMenuItem("Enter left base...", "Runway (optional)", input => vm.EnterLeftBaseAsync(cs, init, NullIfEmpty(input))));
        menu.Items.Add(
            CreateInputMenuItem("Enter right base...", "Runway (optional)", input => vm.EnterRightBaseAsync(cs, init, NullIfEmpty(input)))
        );
        menu.Items.Add(
            CreateInputMenuItem("Enter straight-in final...", "Runway (optional)", input => vm.EnterFinalAsync(cs, init, NullIfEmpty(input)))
        );
        return menu;
    }

    private static void AddCommandTextBox(ContextMenu menu, Func<string, Task> onSubmit)
    {
        var textBox = new TextBox
        {
            Watermark = "Command",
            FontSize = 12,
            MinWidth = 160,
        };
        textBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var text = textBox.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    menu.Close();
                    await onSubmit(text);
                }
            }
            else if (e.Key != Key.Escape)
            {
                e.Handled = true;
            }
        };
        menu.Items.Add(textBox);
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
            menu.Items.Add(new Separator());
        }

        if (vm.SelectedAircraft is not null)
        {
            var callsign = vm.SelectedAircraft.Callsign;
            var initials = GetInitials();

            var heading = (int)(Math.Round(GeoMath.BearingTo(vm.SelectedAircraft.Latitude, vm.SelectedAircraft.Longitude, lat, lon) / 5.0) * 5);
            if (heading <= 0)
            {
                heading = 360;
            }

            menu.Items.Add(CreateMenuItem($"Fly heading {heading:D3}", () => vm.FlyHeadingAsync(callsign, initials, heading)));

            if (frdString is not null)
            {
                var target = frdString;
                menu.Items.Add(CreateMenuItem($"Direct to {target}", () => vm.DirectToAsync(callsign, initials, target)));
                menu.Items.Add(CreateMenuItem($"Append direct to {target}", () => vm.AppendDirectToAsync(callsign, initials, target)));
                menu.Items.Add(CreateMenuItem($"Hold at {target} (left)", () => vm.HoldAtFixLeftAsync(callsign, initials, target)));
                menu.Items.Add(CreateMenuItem($"Hold at {target} (right)", () => vm.HoldAtFixRightAsync(callsign, initials, target)));

                var warpFrd = target;
                var warpHdg = (int)Math.Round(vm.SelectedAircraft.Heading);
                if (warpHdg <= 0)
                {
                    warpHdg = 360;
                }

                var warpAlt = (int)Math.Round(vm.SelectedAircraft.Altitude);
                var warpSpd = (int)Math.Round(vm.SelectedAircraft.IndicatedAirspeed);
                var warpItem = new MenuItem { Header = $"Warp here ({target})" };
                warpItem.Click += (_, _) =>
                {
                    ShowWarpPopup(callsign, warpFrd, warpHdg, warpAlt, warpSpd, (frd, h, a, s) => _ = vm.WarpAsync(callsign, initials, frd, h, a, s));
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

    private MenuItem CreateInputMenuItem(string header, string placeholder, Func<string, Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            ShowInputPopup(placeholder, action);
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
            ShowFilteredListPopup(sortedNames, action, priorityItems);
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
        };
        return item;
    }
}
