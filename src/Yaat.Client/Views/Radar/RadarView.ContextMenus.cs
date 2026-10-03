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
        return AircraftMenuBuilder.Build(
            ac,
            new MenuClick(callsign, prevSelected, null, []),
            host,
            context => BuildViewSection(vm, ac, context, host)
        );
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
        if (DataContext is RadarViewModel vm)
        {
            ShowContextMenu(BuildMapPointMenu(vm, new LatLon(lat, lon), screenPos));
        }
    }

    /// <summary>
    /// The menu a right-click on empty map at <paramref name="position"/> shows, built without opening it. With an
    /// aircraft selected, the shared point menu (<see cref="AircraftMenuBuilder"/> with the clicked position) carries the
    /// aircraft's point items, then the radar's point section (<see cref="BuildMapPointSection"/>); with nothing selected,
    /// or without a main view model (logged), the point section alone.
    /// </summary>
    internal ContextMenu BuildMapPointMenu(RadarViewModel vm, LatLon position, Point screenPos)
    {
        List<Control> section = BuildMapPointSection(vm, position, screenPos);
        if (vm.SelectedAircraft is not { } selected)
        {
            return MenuOf(section);
        }

        if (FindMainViewModel() is not { } main)
        {
            MenuLog.LogWarning(
                "Map right-click with {Callsign} selected: the radar view has no main view model, so only the map items open",
                selected.Callsign
            );
            return MenuOf(section);
        }

        var host = new ClientMenuHost(main, selected, Canvas);
        var click = new MenuClick(selected.Callsign, null, new MenuPoint(position, null, null, [], null), []);
        return AircraftMenuBuilder.Build(selected, click, host, _ => section);
    }

    /// <summary>
    /// The radar's point section for a map right-click at <paramref name="position"/>: the point's FRD row, Copy FRD
    /// and the scope-marker pins when the fixes name the point, the measuring tool's items, then the charted MVA there.
    /// </summary>
    private List<Control> BuildMapPointSection(RadarViewModel vm, LatLon position, Point screenPos)
    {
        List<Control> items = [];
        string? frdString = (vm.Fixes is { } fixes) ? FrdResolver.ToFrd(position.Lat, position.Lon, fixes) : null;
        if (frdString is not null)
        {
            AddFrdItems(items, vm, position, frdString);
        }

        AddMeasureMenuItems(items, vm, RblEndpoint.AtPoint(position, frdString ?? ""), screenPos);

        // MVA at the clicked point (FAA-charted; only the loaded facility's coverage, null elsewhere).
        MvaSector? mvaSector = MvaDatabase.Default.FindSector(position);
        items.Add(
            new MenuItem
            {
                Header = mvaSector is null ? "MVA: no data here" : $"MVA {mvaSector.FloorFtMsl} ft ({mvaSector.Sector})",
                IsEnabled = false,
            }
        );
        return items;
    }

    /// <summary>
    /// The point's <paramref name="frd"/> as a bold row, Copy FRD, then the scope-marker pins (CRC ".ff"/".marker"): pin
    /// one here, remove the one near the click, clear them all; then a separator.
    /// </summary>
    private void AddFrdItems(List<Control> items, RadarViewModel vm, LatLon position, string frd)
    {
        items.Add(
            new MenuItem
            {
                Header = frd,
                IsEnabled = false,
                FontWeight = Avalonia.Media.FontWeight.Bold,
            }
        );
        items.Add(
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

        // The pick radius scales with the current range.
        double pickNm = Math.Max(0.5, vm.RangeNm * 0.04);
        bool nearPin = (vm.PinnedMarkers is { } pins) && pins.Any(m => GeoMath.DistanceNm(position, new LatLon(m.Lat, m.Lon)) <= pickNm);

        items.Add(CreateMenuItem("Pin marker here", () => vm.AddMarker(frd)));
        if (nearPin)
        {
            items.Add(CreateMenuItem("Remove marker", () => vm.RemoveNearestMarker(position.Lat, position.Lon, pickNm)));
        }
        if (vm.HasMarkers)
        {
            items.Add(CreateMenuItem("Clear pinned markers", () => vm.ClearMarkers()));
        }

        items.Add(new Separator());
    }

    private static ContextMenu MenuOf(IEnumerable<Control> items)
    {
        var menu = new ContextMenu();
        foreach (Control item in items)
        {
            menu.Items.Add(item);
        }

        return menu;
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
    private void AddMeasureMenuItems(List<Control> items, RadarViewModel vm, RblEndpoint endpoint, Point screenPos)
    {
        if (vm.Measure is not { } measure)
        {
            return;
        }

        string startLabel = measure.Anchor is null ? "Measure from here" : "Measure to here";
        items.Add(
            CreateMenuItem(startLabel, () => measure.Pick(endpoint, RadarViewModel.MeasureView, vm.MeasureTrackLookup, RadarViewModel.MeasureUnits))
        );

        if (_canvas?.MeasurementSlotAt(screenPos) is { } slot)
        {
            items.Add(CreateMenuItem($"Remove measurement {slot}", () => measure.Remove(slot)));
        }

        if (measure.HasLines)
        {
            items.Add(CreateMenuItem("Clear measurements", measure.Clear));
        }

        items.Add(new Separator());
    }
}
