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

    /// <summary>
    /// The radar's own rows under the shared header's title: the route summary and hold status. None without an
    /// aircraft model. The release items are the shared header's, not the radar's.
    /// </summary>
    private List<MenuItem> RadarTitleRows(AircraftModel? ac)
    {
        List<MenuItem> rows = [];
        if (ac is null)
        {
            return rows;
        }

        if (BuildRouteSummaryItem(ac) is { } routeItem)
        {
            rows.Add(routeItem);
        }

        if (BuildHoldStatusItem(ac) is { } holdItem)
        {
            rows.Add(holdItem);
        }

        return rows;
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
            main?.VfrCommandsForIfr ?? VfrCommandsForIfr.EnterFinalOnly,
            MenuView.Radar
        );
        var host = new RadarMenuHost(this, vm, main, ac);
        var menu = new ContextMenu();
        SharedMenuGroups.AddHeader(menu.Items, ac, context, host, RadarTitleRows(ac));
        menu.Items.Add(SharedMenuGroups.Favorites(ac, context, host));
        menu.Items.Add(new Separator());

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

        SharedMenuGroups.AddRelative(menu.Items, ac, context, host);
        AddAircraftCommandGroups(menu, ac, context, host);
        return menu;
    }

    /// <summary>
    /// A surface live-traffic shadow is never assumable: its menu is read-only — the display groups, then the foot,
    /// which offers it no Warp.
    /// </summary>
    private void AddSurfaceShadowItems(ContextMenu menu, AircraftModel ac, MenuContext context, RadarMenuHost host)
    {
        string callsign = context.Callsign;
        menu.Items.Add(SharedMenuGroups.Track(ac, context, host, MenuView.Radar));
        menu.Items.Add(SharedMenuGroups.DataBlock(ac, context, host));
        menu.Items.Add(SharedMenuGroups.Coordination(ac, context, host));
        menu.Items.Add(SharedMenuGroups.Display(ac, context, host));
        SharedMenuGroups.AddFoot(menu.Items, ac, context, host);
        FindMainViewModel()?.BuildRpoMenuItems(menu, [callsign]);
    }

    /// <summary>
    /// The phase-aware command groups, the always-visible track / data block / squawk / coordination / display
    /// submenus and the foot (Warp, release to live feed, Delete), exactly as a simulated aircraft gets them. For a
    /// live-traffic shadow the read-only ask-pilot queries stay out
    /// (<see cref="AircraftCommandApplicability.CanAskPilot"/>); everything else, Warp included, applies, because it
    /// goes through the command path and so auto-assumes the shadow first.
    /// </summary>
    private void AddAircraftCommandGroups(ContextMenu menu, AircraftModel? ac, MenuContext context, RadarMenuHost host)
    {
        string callsign = context.Callsign;
        ContextMenuProfile profile = ContextMenuProfileService.GetProfile(ac?.CurrentPhase, ac?.IsOnGround ?? false);

        foreach (MenuGroup group in profile.PrimaryGroups)
        {
            AddMenuGroup(menu, group, ac, context, host);
        }

        if (profile.PrimaryGroups.Count > 0 && profile.SecondaryGroups.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        foreach (MenuGroup group in profile.SecondaryGroups)
        {
            AddMenuGroup(menu, group, ac, context, host);
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
        SharedMenuGroups.AddFoot(menu.Items, ac, context, host);

        // RPO control
        FindMainViewModel()?.BuildRpoMenuItems(menu, [callsign]);
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

    private static void AddMenuGroup(ContextMenu menu, MenuGroup group, AircraftModel? ac, MenuContext context, RadarMenuHost host)
    {
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
                MenuItem? tower = SharedMenuGroups.Tower(ac, context, host);
                if (tower is not null)
                {
                    menu.Items.Add(tower);
                }
                break;
            case MenuGroup.Pattern:
                MenuItem? pattern = SharedMenuGroups.Pattern(ac, context, host);
                if (pattern is not null)
                {
                    menu.Items.Add(pattern);
                }
                break;
        }
    }

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
}
