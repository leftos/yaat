using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.Services;

/// <summary>
/// Captures and applies user-managed saved layouts. A layout is a named
/// snapshot of every live window's geometry plus the six pop-out toggles driven from
/// <see cref="MainViewModel"/>, the extra Radar/Ground windows, favorites state, the open
/// Strips / vTDLS tabs and the DataGrid column layout.
///
/// Capture walks <see cref="WindowGeometryHelper.GetActiveHelpers"/> to read every
/// open window's current geometry, then snapshots the pop-out toggles and the
/// most-recent DataGrid layout already in <see cref="UserPreferences.GridLayout"/>
/// (kept fresh by the column-reorder/sort/resize handlers).
///
/// Apply works in two passes: first it pre-stamps the per-window geometry
/// preferences and DataGrid layout so any pop-out window that the layout opens
/// reads the new values via the normal <see cref="WindowGeometryHelper.Restore"/>
/// path, then it flips the pop-out toggles on the MainViewModel so the existing
/// handlers in MainWindow create / destroy the appropriate pop-outs. A final
/// caller-side pass (in MainWindow) sweeps still-open windows to push the new
/// geometry onto them via <see cref="WindowGeometryHelper.ApplyGeometry"/>.
/// </summary>
public sealed class LayoutService(UserPreferences preferences)
{
    private static readonly ILogger Log = AppLog.CreateLogger<LayoutService>();

    private readonly UserPreferences _preferences = preferences;

    /// <summary>
    /// Builds a layout from the current state of every live window plus the
    /// MainViewModel pop-out toggles and the cached DataGrid layout. Does not
    /// persist — caller passes the result to <see cref="UserPreferences.SaveLayout"/>.
    /// </summary>
    public SavedLayout CaptureCurrent(string name, MainViewModel vm)
    {
        var layout = new SavedLayout
        {
            Name = name.Trim(),
            IsTerminalPoppedOut = vm.IsTerminalPoppedOut,
            IsDataGridPoppedOut = vm.IsDataGridPoppedOut,
            IsGroundViewPoppedOut = vm.IsGroundViewPoppedOut,
            IsRadarViewPoppedOut = vm.IsRadarViewPoppedOut,
            IsControllersPoppedOut = vm.IsControllersPoppedOut,
            IsMetarPoppedOut = vm.IsMetarPoppedOut,
            DataGridLayout = CloneGridLayout(_preferences.GridLayout),
            LoadedFavoriteSetIds = [.. _preferences.LoadedFavoriteSetIds],
            ShowFavoritesBar = vm.ShowFavoritesBar,
            IsFavoritesPanelOpen = FavoritesPanelWindow.IsOpen(vm),
            // Which extra Radar/Ground windows are open and on which airport; their geometry rides the
            // live-helper walk below under the RadarView#n / GroundView#n keys.
            ExtraRadarViews = [.. vm.ExtraRadarViews.OrderBy(i => i.Ordinal).Select(i => new SavedExtraView(i.Ordinal, i.AirportId))],
            ExtraGroundViews = [.. vm.ExtraGroundViews.OrderBy(i => i.Ordinal).Select(i => new SavedExtraView(i.Ordinal, i.AirportId))],
            OpenTabs = CaptureOpenTabs(vm),
        };

        // Flush every open window's helper first so the snapshot we read back
        // from prefs reflects the current on-screen position, not the position
        // saved the last time the window was closed.
        WindowGeometryHelper.FlushAllSavedGeometries();

        foreach (WindowGeometryHelper helper in WindowGeometryHelper.GetActiveHelpers())
        {
            SavedWindowGeometry? geo = _preferences.GetWindowGeometry(helper.WindowName);
            if (geo is null)
            {
                continue;
            }
            layout.WindowGeometries[helper.WindowName] = Clone(geo);
        }

        Log.LogInformation("Captured layout '{Name}' with {Count} window geometries", layout.Name, layout.WindowGeometries.Count);
        return layout;
    }

    /// <summary>
    /// Pre-applies a layout by writing the geometries named in <paramref name="selectedGeometryKeys"/> into the
    /// per-window preferences, and replacing the cached DataGrid layout when <paramref name="includeGrid"/> is set.
    /// Pop-out windows the apply re-opens read these values on construction. A whole-layout apply passes every
    /// geometry key; the apply dialog passes the rows the user checked.
    /// </summary>
    public void StagePreferences(SavedLayout layout, IReadOnlySet<string> selectedGeometryKeys, bool includeGrid)
    {
        int staged = 0;
        foreach ((string? key, SavedWindowGeometry? geo) in layout.WindowGeometries)
        {
            if (!selectedGeometryKeys.Contains(key))
            {
                continue;
            }

            _preferences.SetWindowGeometry(key, Clone(geo));
            staged++;
        }

        if (includeGrid && layout.DataGridLayout is not null)
        {
            _preferences.SetGridLayout(CloneGridLayout(layout.DataGridLayout) ?? new SavedGridLayout());
        }

        Log.LogInformation(
            "Staged {Staged} of {Total} geometries from layout '{Name}' (grid={Grid})",
            staged,
            layout.WindowGeometries.Count,
            layout.Name,
            includeGrid
        );
    }

    /// <summary>
    /// Opens and closes Strips and vTDLS tabs to match the layout's <see cref="SavedLayout.OpenTabs"/>. A tab already
    /// open on a listed facility stays as it is; a listed facility the student position can no longer open is skipped.
    /// A layout saved without open tabs (null) leaves every tab as it is. The student's own tab is never touched.
    /// </summary>
    /// <param name="layout">The layout being applied.</param>
    /// <param name="vm">The main view model owning the tabs.</param>
    /// <returns>The facility ids skipped because the student position can no longer open them, Strips first.</returns>
    public async Task<IReadOnlyList<string>> ApplyOpenTabsAsync(SavedLayout layout, MainViewModel vm)
    {
        if (layout.OpenTabs is not { } tabs)
        {
            return [];
        }

        // An empty facility list means no room (or one not loaded yet), not that every facility became unavailable:
        // nothing can be judged, so every tab stays as it is and nothing is reported.
        if (vm.VStrips.AccessibleFacilities.Count == 0)
        {
            Log.LogInformation("Open tabs of layout '{Name}' not applied: no accessible facilities (no room)", layout.Name);
            return [];
        }

        List<string> skipped = [];
        await ApplyStripsTabsAsync(vm, tabs.Strips, skipped);
        await ApplyTdlsTabsAsync(vm, tabs.Tdls, skipped);

        Log.LogInformation(
            "Applied open tabs from layout '{Name}' ({Strips} strips, {Tdls} vTDLS listed, {Skipped} skipped)",
            layout.Name,
            tabs.Strips.Count,
            tabs.Tdls.Count,
            skipped.Count
        );
        return skipped;
    }

    private static async Task ApplyStripsTabsAsync(MainViewModel vm, List<string> listed, List<string> skipped)
    {
        List<string> toOpen = KeepListedTabs(
            [.. vm.StripsEntries.Where(e => !e.IsStudentEntry).Select(e => (e.Vm.FacilityId, Close: (Action)(() => vm.CloseStripsEntry(e))))],
            listed
        );
        HashSet<string> accessible = AccessibleIds(vm.VStrips.AccessibleFacilities);
        HashSet<string> studentOwn = AccessibleIds(vm.VStrips.AccessibleFacilities.Where(f => f.IsStudentFacility));
        foreach (string facilityId in toOpen)
        {
            // The student's own facility always has its tab, as with vTDLS: no second tab is opened for it.
            if (studentOwn.Contains(facilityId))
            {
                continue;
            }

            if (!accessible.Contains(facilityId))
            {
                skipped.Add(facilityId);
                continue;
            }

            await vm.OpenStripsEntryForFacilityAsync(facilityId);
        }
    }

    private static async Task ApplyTdlsTabsAsync(MainViewModel vm, List<string> listed, List<string> skipped)
    {
        List<string> toOpen = KeepListedTabs(
            [.. vm.TdlsEntries.Where(e => !e.IsStudentEntry).Select(e => (e.Vm.FacilityId, Close: (Action)(() => vm.CloseTdlsEntry(e))))],
            listed
        );
        HashSet<string> accessible = AccessibleIds(vm.VTdls.AccessibleFacilities);
        foreach (string facilityId in toOpen)
        {
            // vTDLS keeps one tab per facility, the student's included: a facility already shown needs no new tab.
            if (vm.FindTdlsEntry(facilityId) is not null)
            {
                continue;
            }

            if (!accessible.Contains(facilityId))
            {
                skipped.Add(facilityId);
                continue;
            }

            await vm.OpenTdlsEntryForFacilityAsync(facilityId);
        }
    }

    private static SavedOpenTabs CaptureOpenTabs(MainViewModel vm) =>
        new()
        {
            Strips = [.. vm.StripsEntries.Where(e => !e.IsStudentEntry).Select(e => e.Vm.FacilityId).OfType<string>().Where(id => id.Length > 0)],
            Tdls = [.. vm.TdlsEntries.Where(e => !e.IsStudentEntry).Select(e => e.Vm.FacilityId).OfType<string>().Where(id => id.Length > 0)],
        };

    /// <summary>
    /// Closes every open tab the listed facility ids do not account for, one listed id per open tab, and returns the
    /// listed ids no open tab accounts for, in list order: the tabs still to open.
    /// A tab with no FacilityId never matches and is closed.
    /// </summary>
    private static List<string> KeepListedTabs(List<(string? FacilityId, Action Close)> openTabs, List<string> listed)
    {
        List<string> unmatched = [.. listed];
        foreach ((string? facilityId, Action close) in openTabs)
        {
            int match = unmatched.FindIndex(id => string.Equals(id, facilityId, StringComparison.OrdinalIgnoreCase));
            if (match >= 0)
            {
                unmatched.RemoveAt(match);
            }
            else
            {
                close();
            }
        }

        return unmatched;
    }

    private static HashSet<string> AccessibleIds(IEnumerable<AccessibleFacilityDto> facilities) =>
        new(facilities.Select(f => f.FacilityId), StringComparer.OrdinalIgnoreCase);

    private static SavedWindowGeometry Clone(SavedWindowGeometry source) =>
        new()
        {
            X = source.X,
            Y = source.Y,
            Width = source.Width,
            Height = source.Height,
            IsMaximized = source.IsMaximized,
            IsMinimized = source.IsMinimized,
            ScreenIndex = source.ScreenIndex,
            IsTopmost = source.IsTopmost,
        };

    private static SavedGridLayout? CloneGridLayout(SavedGridLayout? source)
    {
        if (source is null)
        {
            return null;
        }
        return new SavedGridLayout
        {
            ColumnOrder = source.ColumnOrder is null ? null : [.. source.ColumnOrder],
            SortColumn = source.SortColumn,
            SortDirection = source.SortDirection,
            ColumnWidths = source.ColumnWidths is null ? null : new Dictionary<string, double>(source.ColumnWidths),
            HiddenColumns = source.HiddenColumns is null ? null : [.. source.HiddenColumns],
        };
    }
}
