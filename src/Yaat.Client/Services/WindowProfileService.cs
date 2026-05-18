using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.Services;

/// <summary>
/// Captures and applies user-managed window-layout profiles. A profile is a named
/// snapshot of every live window's geometry plus the four pop-out / dock toggles
/// driven from <see cref="MainViewModel"/> and the DataGrid column layout.
///
/// Capture walks <see cref="WindowGeometryHelper.GetActiveHelpers"/> to read every
/// open window's current geometry, then snapshots the pop-out toggles and the
/// most-recent DataGrid layout already in <see cref="UserPreferences.GridLayout"/>
/// (kept fresh by the column-reorder/sort/resize handlers).
///
/// Apply works in two passes: first it pre-stamps the per-window geometry
/// preferences and DataGrid layout so any pop-out window that the profile opens
/// reads the new values via the normal <see cref="WindowGeometryHelper.Restore"/>
/// path, then it flips the pop-out toggles on the MainViewModel so the existing
/// handlers in MainWindow create / destroy the appropriate pop-outs. A final
/// caller-side pass (in MainWindow) sweeps still-open windows to push the new
/// geometry onto them via <see cref="WindowGeometryHelper.ApplyGeometry"/>.
/// </summary>
public sealed class WindowProfileService
{
    private static readonly ILogger Log = AppLog.CreateLogger<WindowProfileService>();

    private readonly UserPreferences _preferences;

    public WindowProfileService(UserPreferences preferences)
    {
        _preferences = preferences;
    }

    /// <summary>
    /// Builds a profile from the current state of every live window plus the
    /// MainViewModel pop-out toggles and the cached DataGrid layout. Does not
    /// persist — caller passes the result to <see cref="UserPreferences.SaveWindowProfile"/>.
    /// </summary>
    public SavedWindowProfile CaptureCurrent(string name, MainViewModel vm)
    {
        var profile = new SavedWindowProfile
        {
            Name = name.Trim(),
            IsTerminalDocked = vm.IsTerminalDocked,
            IsDataGridPoppedOut = vm.IsDataGridPoppedOut,
            IsGroundViewPoppedOut = vm.IsGroundViewPoppedOut,
            IsRadarViewPoppedOut = vm.IsRadarViewPoppedOut,
            DataGridLayout = CloneGridLayout(_preferences.GridLayout),
        };

        // Flush every open window's helper first so the snapshot we read back
        // from prefs reflects the current on-screen position, not the position
        // saved the last time the window was closed.
        WindowGeometryHelper.FlushAllSavedGeometries();

        foreach (var helper in WindowGeometryHelper.GetActiveHelpers())
        {
            var geo = _preferences.GetWindowGeometry(helper.WindowName);
            if (geo is null)
            {
                continue;
            }
            profile.WindowGeometries[helper.WindowName] = Clone(geo);
        }

        Log.LogInformation("Captured window profile '{Name}' with {Count} window geometries", profile.Name, profile.WindowGeometries.Count);
        return profile;
    }

    /// <summary>
    /// Pre-applies a profile by writing each geometry into the per-window
    /// preferences and replacing the cached DataGrid layout. Pop-out windows
    /// that the profile re-opens will read these values on construction.
    /// Returns the list of helper keys that were live before the apply, so the
    /// caller knows which already-open windows need their geometry pushed by
    /// <see cref="WindowGeometryHelper.ApplyGeometry"/> after the toggle flips.
    /// </summary>
    public IReadOnlyList<string> StagePreferences(SavedWindowProfile profile)
    {
        // Snapshot live helpers before we mutate prefs so the caller can
        // distinguish "already open, just reposition" from "about to open via
        // a pop-out toggle, geometry will arrive via Restore()".
        var liveKeys = WindowGeometryHelper.GetActiveHelpers().Select(h => h.WindowName).ToArray();

        foreach (var (key, geo) in profile.WindowGeometries)
        {
            _preferences.SetWindowGeometry(key, Clone(geo));
        }

        if (profile.DataGridLayout is not null)
        {
            _preferences.SetGridLayout(CloneGridLayout(profile.DataGridLayout) ?? new SavedGridLayout());
        }

        Log.LogInformation("Staged window profile '{Name}' into preferences ({Live} live windows snapshotted)", profile.Name, liveKeys.Length);
        return liveKeys;
    }

    private static SavedWindowGeometry Clone(SavedWindowGeometry source) =>
        new()
        {
            X = source.X,
            Y = source.Y,
            Width = source.Width,
            Height = source.Height,
            IsMaximized = source.IsMaximized,
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
