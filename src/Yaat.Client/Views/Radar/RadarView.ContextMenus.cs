using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
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
    private static readonly ILogger MenuLog = AppLog.CreateLogger("RadarView.ContextMenus");

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

        if (FindMainViewModel() is not { } main)
        {
            MenuLog.LogWarning("Right-click on {Callsign}: the radar view has no main view model, so no aircraft menu opens", callsign);
            return;
        }

        AircraftModel? ac = main.Aircraft.FirstOrDefault(a => a.Callsign == callsign);

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

        ShowContextMenu(BuildAircraftContextMenu(vm, ac, prevSelected, callsign));
    }

    /// <summary>
    /// The whole aircraft context menu a right-click shows, built without opening it through
    /// <see cref="AircraftMenuBuilder"/>: the right-clicked <paramref name="callsign"/> and its model
    /// <paramref name="ac"/>, the selected aircraft <paramref name="prevSelected"/> that relative actions target, and
    /// the radar's view section (<see cref="BuildViewSection"/>). Touches no canvas, popup or pointer state.
    /// </summary>
    internal ContextMenu BuildAircraftContextMenu(RadarViewModel vm, AircraftModel? ac, AircraftModel? prevSelected, string callsign)
    {
        MainViewModel main =
            FindMainViewModel()
            ?? throw new InvalidOperationException("The radar aircraft menu needs the main view model; the radar view is not hosted by one");
        var host = new ClientMenuHost(main, ac, Canvas);
        return AircraftMenuBuilder.Build(ac, new MenuClick(callsign, prevSelected, []), host, context => BuildViewSection(vm, ac, context, host));
    }

    /// <summary>
    /// The radar's view section: its Display submenu (<see cref="BuildCanvasDisplay"/>), then Draw route. A surface
    /// live-traffic shadow — read-only, so nothing may command it — goes without Draw route, and so does an aircraft
    /// whose phase hides the flight commands (<see cref="ContextMenuProfileService"/>: on the ground, landing or rolling).
    /// </summary>
    internal IReadOnlyList<Control> BuildViewSection(RadarViewModel vm, AircraftModel? ac, MenuContext context, IMenuHost host)
    {
        MenuItem display = BuildCanvasDisplay(vm, context, host);
        bool surfaceShadow = AircraftCommandApplicability.IsSurfaceShadow(ac);
        bool flightCommandsHidden = ContextMenuProfileService
            .GetProfile(ac?.CurrentPhase, ac?.IsOnGround ?? false)
            .HiddenGroups.Contains(MenuGroup.Navigation);
        if (surfaceShadow || flightCommandsHidden)
        {
            return [display];
        }

        string callsign = context.Callsign;
        return [display, CanvasMenuItems.DrawRoute("Draw route", () => vm.EnterDrawRoute(callsign))];
    }

    /// <summary>
    /// The radar canvas's Display submenu, built from the radar's own state: the data-block form and its position
    /// reset, the nav route and the measurement in progress, then the leader-direction, J-ring and cone overlays, then
    /// blank and unblank. It opens the radar's view section, on every radar aircraft menu.
    /// </summary>
    internal MenuItem BuildCanvasDisplay(RadarViewModel vm, MenuContext context, IMenuHost host)
    {
        string callsign = context.Callsign;
        return CanvasMenuItems.Display([
            [
                CanvasMenuItems.DataBlockForm(Canvas.IsMinified(callsign), () => Canvas.ToggleMinifiedDataBlock(callsign)),
                CanvasMenuItems.ResetDataBlockPosition(Canvas.HasManualDataBlockOffset(callsign), () => Canvas.ResetDataBlockOffset(callsign)),
                CanvasMenuItems.NavRoute(vm.IsPathShown(callsign), () => vm.ToggleShowPath(callsign)),
                CanvasMenuItems.Measure(MeasureState(vm), callsign, () => MeasurePickOnAircraft(vm, callsign)),
            ],
            [CanvasMenuItems.LeaderDirection(context, host), CanvasMenuItems.JRing(context, host), CanvasMenuItems.Cone(context, host)],
            [CanvasMenuItems.Blank(context, host), CanvasMenuItems.Unblank(context, host)],
        ]);
    }

    /// <summary>What the radar's measure tool is doing, which decides whether the Display submenu offers a measure item.</summary>
    private static MenuMeasureState MeasureState(RadarViewModel vm) =>
        vm.Measure switch
        {
            null => MenuMeasureState.None,
            { Anchor: null } => MenuMeasureState.NoAnchor,
            _ => MenuMeasureState.HasAnchor,
        };

    /// <summary>Latches the radar's pending measurement to <paramref name="callsign"/>, so the line follows it.</summary>
    private static void MeasurePickOnAircraft(RadarViewModel vm, string callsign)
    {
        if (vm.Measure is { } measure)
        {
            measure.Pick(RblEndpoint.OnAircraft(callsign), RadarViewModel.MeasureView, vm.MeasureTrackLookup, RadarViewModel.MeasureUnits);
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
                    MenuPopups.ShowWarp(
                        Canvas,
                        new MenuPopups.WarpSeed(callsign, warpFrd, warpHdg, warpAlt, warpSpd),
                        (frd, h, a, s) => vm.WarpAsync(callsign, initials, frd, h, a, s)
                    );
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
