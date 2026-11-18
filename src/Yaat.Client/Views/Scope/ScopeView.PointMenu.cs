using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Yaat.Client.ContextMenus;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Map;
using Yaat.Sim;
using Yaat.Sim.Data.Mva;

namespace Yaat.Client.Views.Scope;

/// <summary>
/// The Scope's map right click: the point menu at the clicked point — the point's FRD row with Copy FRD when the fixes name it,
/// the measuring tool's items, then the charted MVA there — with the shared point header and quick strip above it while an
/// aircraft is selected. "Show all datablocks" and "Settings for this view…" end it.
/// </summary>
public partial class ScopeView
{
    private void OnMapRightClicked(LatLon position, Point screenPos)
    {
        if (DataContext is ScopeViewModel vm)
        {
            ShowContextMenu(BuildMapContextMenu(vm, position, screenPos));
        }
    }

    /// <summary>
    /// The map's right-click menu at <paramref name="position"/>: the point menu (<see cref="BuildMapPointMenu"/>), then "Show
    /// all datablocks" and "Settings for this view…", each in its own group.
    /// </summary>
    internal ContextMenu BuildMapContextMenu(ScopeViewModel vm, LatLon position, Point screenPos)
    {
        ContextMenu menu = BuildMapPointMenu(vm, position, screenPos);
        if ((menu.Items.Count > 0) && (menu.Items[^1] is not Separator))
        {
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(CanvasMenuItems.ShowAllDataBlocks(vm.AnyDataBlockHidden, vm.ShowAllDataBlocks));
        ViewSettingsMenu.Append(menu, vm.Main, SettingsSectionId.Radar);
        return menu;
    }

    /// <summary>
    /// The menu a right click on empty map at <paramref name="position"/> shows, built without opening it. With an aircraft
    /// selected, the shared point menu (<see cref="AircraftMenuBuilder"/> with the clicked position) carries the aircraft's
    /// point header and quick strip, then the Scope's point section (<see cref="BuildMapPointSection"/>), without its FRD row
    /// when the header already names the point by it; with nothing selected, the point section alone.
    /// </summary>
    internal ContextMenu BuildMapPointMenu(ScopeViewModel vm, LatLon position, Point screenPos)
    {
        if (vm.SelectedAircraft is not { } selected)
        {
            return MenuOf(BuildMapPointSection(vm, position, screenPos, withFrdRow: true));
        }

        var point = new MenuPoint(position, null, null, [], null);
        ClientMenuHost host = new(vm.Main, selected, Canvas, vm.Main.ScopeMaps.MvaFacilityId);
        List<Control> section = BuildMapPointSection(vm, position, screenPos, !SharedMenuGroups.PointHeaderShowsFrd(point, host));
        var click = new MenuClick(selected.Callsign, null, point, []);
        return AircraftMenuBuilder.Build(selected, click, host, _ => section);
    }

    /// <summary>
    /// The Scope's point section for a map right click at <paramref name="position"/>: the point's FRD row (when
    /// <paramref name="withFrdRow"/>) with Copy FRD and the pinned-marker rows, the measuring tool's items, then the charted
    /// MVA there (<see cref="MvaRow"/>), left out where no MVA sector covers the point.
    /// </summary>
    private List<Control> BuildMapPointSection(ScopeViewModel vm, LatLon position, Point screenPos, bool withFrdRow)
    {
        List<Control> items = [];
        string? frd = vm.Main.ScopeMaps.DescribePoint(position.Lat, position.Lon);
        if (frd is not null)
        {
            AddFrdItems(items, frd, withFrdRow);
            AddMarkerItems(items, vm, position, frd);
            items.Add(new Separator());
        }

        AddMeasureMenuItems(items, vm, RblEndpoint.AtPoint(position, frd ?? ""), screenPos);

        // MVA at the clicked point (FAA-charted; the Scope's own facility chart wins where charts overlap).
        if (MvaDatabase.Default.FindSector(position, vm.Main.ScopeMaps.MvaFacilityId) is { } mvaSector)
        {
            items.Add(SharedMenuGroups.DetailRow(MvaRow(mvaSector)));
        }

        return items;
    }

    /// <summary>The MVA row's text: <c>MVA 3,000 ft (sector 9)</c>, or <c>MVA 3,000 ft</c> for a sector with no name.</summary>
    private static string MvaRow(MvaSector sector)
    {
        string floor = sector.FloorFtMsl.ToString("N0", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(sector.Sector) ? $"MVA {floor} ft" : $"MVA {floor} ft (sector {sector.Sector})";
    }

    /// <summary>
    /// The point's <paramref name="frd"/> as a bold row (when <paramref name="withFrdRow"/>), then Copy FRD.
    /// </summary>
    private void AddFrdItems(List<Control> items, string frd, bool withFrdRow)
    {
        if (withFrdRow)
        {
            items.Add(
                new MenuItem
                {
                    Header = frd,
                    IsEnabled = false,
                    FontWeight = FontWeight.Bold,
                }
            );
        }

        items.Add(CreateMenuItem("Copy FRD", () => CopyFrd(frd)));
    }

    /// <summary>
    /// The pinned-marker rows the point offers: Pin marker here, always, which pins <paramref name="frd"/>; Remove marker, only
    /// while a marker lies within the pick radius of <paramref name="position"/> (4% of the range, at least 0.5 NM), which
    /// removes the nearest one; and Clear pinned markers, only while any marker is pinned.
    /// </summary>
    private static void AddMarkerItems(List<Control> items, ScopeViewModel vm, LatLon position, string frd)
    {
        double pickNm = vm.MarkerPickRadiusNm;
        items.Add(CreateMenuItem("Pin marker here", () => vm.AddMarker(frd)));
        if (vm.HasMarkerNear(position, pickNm))
        {
            items.Add(CreateMenuItem("Remove marker", () => vm.RemoveNearestMarker(position, pickNm)));
        }

        if (vm.HasMarkers)
        {
            items.Add(CreateMenuItem("Clear pinned markers", vm.ClearMarkers));
        }
    }

    /// <summary>
    /// Adds the distance measuring tool's items: start or finish a measurement at <paramref name="endpoint"/>, remove the one
    /// the click landed on, and clear them all.
    /// </summary>
    private void AddMeasureMenuItems(List<Control> items, ScopeViewModel vm, RblEndpoint endpoint, Point screenPos)
    {
        // An anchor another Scope window holds counts as none here: a pick here anchors anew.
        string startLabel = vm.Measure.HasAnchorFor(ScopeViewModel.MeasureView, vm) ? "Measure to here" : "Measure from here";
        items.Add(CreateMenuItem(startLabel, () => vm.PickMeasurePoint(endpoint)));

        if (Canvas.MeasurementSlotAt(screenPos) is { } slot)
        {
            items.Add(CreateMenuItem($"Remove measurement {slot}", () => vm.Measure.Remove(slot)));
        }

        if (vm.Measure.HasLines)
        {
            items.Add(CreateMenuItem("Clear measurements", vm.Measure.Clear));
        }

        items.Add(new Separator());
    }

    private async Task CopyFrd(string frd)
    {
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(frd);
        }
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
}
