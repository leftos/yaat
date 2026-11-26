using System.Globalization;
using Yaat.Client.Models;

namespace Yaat.Client.Services;

/// <summary>
/// One selectable section of per-scenario Ground view settings. <see cref="Copy"/> moves
/// just this section's fields from a source settings object into a target; <see cref="AreEqual"/>
/// does the structural diff that drives the "differs" indicator; <see cref="Describe"/> renders
/// the compact value shown in the comparison table.
/// </summary>
public sealed class GroundCopyGroup
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required Func<SavedGroundSettings, string> Describe { get; init; }
    public required Func<SavedGroundSettings, SavedGroundSettings, bool> AreEqual { get; init; }
    public required Action<SavedGroundSettings, SavedGroundSettings> Copy { get; init; }
}

/// <summary>
/// One selectable row of a saved layout in the apply dialog: a pop-out window, the extra views, favorites, open tabs or
/// the aircraft-list columns. <see cref="IsSaved"/> is false when the layout predates the state being captured (a null
/// field), which leaves nothing to apply. Applying a row is <c>MainWindow</c>'s, keyed by <see cref="Key"/>.
/// </summary>
public sealed class LayoutCopyGroup
{
    public required string Key { get; init; }
    public required string Section { get; init; }
    public required string Label { get; init; }
    public required Func<SavedLayout, string> Describe { get; init; }
    public required Func<SavedLayout, SavedLayout, bool> AreEqual { get; init; }
    public Func<SavedLayout, bool> IsSaved { get; init; } = _ => true;
}

/// <summary>
/// Single source of truth for how per-scenario Ground view settings and saved layouts are grouped when applied
/// from a source. Used by both <c>ApplyLayoutDialog</c> (to render the comparison rows) and the apply step in
/// <c>MainWindow</c> (to merge the selected sections into the current settings), so the groupings can never drift apart.
/// </summary>
public static class ViewSettingsCopyCatalog
{
    /// <summary>A layout's window-geometry rows are keyed by this prefix plus the window name.</summary>
    public const string LayoutGeometryKeyPrefix = "geo:";

    public const string LayoutTerminalKey = "layout.terminal";
    public const string LayoutAircraftListKey = "layout.aircraftList";
    public const string LayoutGroundKey = "layout.ground";
    public const string LayoutScopeKey = "layout.scope";
    public const string LayoutControllersKey = "layout.controllers";
    public const string LayoutMetarKey = "layout.metar";
    public const string LayoutExtraScopeKey = "layout.extraScope";
    public const string LayoutExtraGroundKey = "layout.extraGround";
    public const string LayoutFavoriteSetsKey = "layout.favoriteSets";
    public const string LayoutFavoritesBarKey = "layout.favoritesBar";
    public const string LayoutFavoritesPanelKey = "layout.favoritesPanel";
    public const string LayoutRundownsWindowKey = "layout.rundownsWindow";
    public const string LayoutOpenTabsKey = "layout.openTabs";
    public const string LayoutColumnsKey = "layout.columns";

    /// <summary>The label of the primary Scope's pop-out row.</summary>
    public const string ScopeViewLabel = "Radar view";

    /// <summary>The label of the extra Scope windows' row.</summary>
    public const string ExtraScopeWindowsLabel = "Extra Radar windows";

    private const string NotSaved = "not saved";

    public static IReadOnlyList<LayoutCopyGroup> LayoutGroups { get; } =
    [
        new LayoutCopyGroup
        {
            Key = LayoutTerminalKey,
            Section = "Pop-out windows",
            Label = "Terminal",
            Describe = DescribeTerminal,
            AreEqual = (a, b) =>
                (a.IsTerminalPoppedOut == b.IsTerminalPoppedOut) && (a.IsTerminalPoppedOut || RatioEqual(a.TerminalSplitRatio, b.TerminalSplitRatio)),
        },
        PopOutGroup(LayoutAircraftListKey, "Aircraft list", l => l.IsDataGridPoppedOut),
        PopOutGroup(LayoutGroundKey, "Ground view", l => l.IsGroundViewPoppedOut),
        new LayoutCopyGroup
        {
            Key = LayoutScopeKey,
            Section = "Pop-out windows",
            Label = ScopeViewLabel,
            Describe = l => l.IsScopePoppedOut is { } poppedOut ? (poppedOut ? "popped out" : "docked") : NotSaved,
            AreEqual = (a, b) => (a.IsScopePoppedOut is null) || (b.IsScopePoppedOut is null) || (a.IsScopePoppedOut == b.IsScopePoppedOut),
            IsSaved = l => l.IsScopePoppedOut is not null,
        },
        PopOutGroup(LayoutControllersKey, "Controllers", l => l.IsControllersPoppedOut),
        PopOutGroup(LayoutMetarKey, "METAR", l => l.IsMetarPoppedOut),
        new LayoutCopyGroup
        {
            Key = LayoutExtraScopeKey,
            Section = "Extra windows",
            Label = ExtraScopeWindowsLabel,
            Describe = l => l.ExtraScopeViews is { } views ? FormatScopeExtras(views) : NotSaved,
            AreEqual = (a, b) => ScopeExtrasEqual(a.ExtraScopeViews, b.ExtraScopeViews),
            IsSaved = l => l.ExtraScopeViews is not null,
        },
        new LayoutCopyGroup
        {
            Key = LayoutExtraGroundKey,
            Section = "Extra windows",
            Label = "Extra ground windows",
            Describe = l => FormatExtraViews(l.ExtraGroundViews),
            AreEqual = (a, b) => a.ExtraGroundViews.SequenceEqual(b.ExtraGroundViews),
        },
        new LayoutCopyGroup
        {
            Key = LayoutFavoriteSetsKey,
            Section = "Favorites",
            Label = "Loaded favorite sets",
            Describe = l =>
                (l.LoadedFavoriteSetIds is { } ids ? $"{ids.Count} set{Plural(ids.Count)}" : NotSaved)
                + ((l.ShowGlobalFavoriteSet == false) ? ", Global off" : ""),
            AreEqual = (a, b) =>
                SequenceEqual(a.LoadedFavoriteSetIds, b.LoadedFavoriteSetIds)
                && SequenceEqual(a.FavoriteSetOrder, b.FavoriteSetOrder)
                && (a.ShowGlobalFavoriteSet == b.ShowGlobalFavoriteSet),
            IsSaved = l => (l.LoadedFavoriteSetIds is not null) || (l.FavoriteSetOrder is not null) || (l.ShowGlobalFavoriteSet is not null),
        },
        new LayoutCopyGroup
        {
            Key = LayoutFavoritesBarKey,
            Section = "Favorites",
            Label = "Favorites bar",
            Describe = l => l.ShowFavoritesBar is { } shown ? (shown ? "shown" : "hidden") : NotSaved,
            AreEqual = (a, b) => a.ShowFavoritesBar == b.ShowFavoritesBar,
            IsSaved = l => l.ShowFavoritesBar is not null,
        },
        new LayoutCopyGroup
        {
            Key = LayoutFavoritesPanelKey,
            Section = "Favorites",
            Label = "Favorites panel",
            Describe = l => l.IsFavoritesPanelOpen is { } open ? (open ? "open" : "closed") : NotSaved,
            AreEqual = (a, b) => a.IsFavoritesPanelOpen == b.IsFavoritesPanelOpen,
            IsSaved = l => l.IsFavoritesPanelOpen is not null,
        },
        new LayoutCopyGroup
        {
            Key = LayoutRundownsWindowKey,
            Section = "Tool windows",
            Label = "Rundowns window",
            Describe = l => l.IsRundownsWindowOpen is { } open ? (open ? "open" : "closed") : NotSaved,
            AreEqual = (a, b) => a.IsRundownsWindowOpen == b.IsRundownsWindowOpen,
            IsSaved = l => l.IsRundownsWindowOpen is not null,
        },
        new LayoutCopyGroup
        {
            Key = LayoutOpenTabsKey,
            Section = "Tabs",
            Label = "Strips / vTDLS tabs",
            Describe = l => l.OpenTabs is { } tabs ? FormatOpenTabs(tabs) : NotSaved,
            AreEqual = (a, b) => OpenTabsEqual(a.OpenTabs, b.OpenTabs),
            IsSaved = l => l.OpenTabs is not null,
        },
        new LayoutCopyGroup
        {
            Key = LayoutColumnsKey,
            Section = "Aircraft list",
            Label = "Column layout",
            Describe = l => FormatGrid(l.DataGridLayout),
            AreEqual = (a, b) => GridEqual(a.DataGridLayout, b.DataGridLayout),
        },
    ];

    /// <summary>Every row key a whole-layout apply selects: each <see cref="LayoutGroups"/> row and each saved window geometry.</summary>
    public static HashSet<string> AllLayoutKeys(SavedLayout layout) =>
        [.. LayoutGroups.Select(g => g.Key), .. layout.WindowGeometries.Keys.Select(k => LayoutGeometryKeyPrefix + k)];

    /// <summary>Position group keys carry an airport-aware label and the cross-airport warning.</summary>
    public const string GroundPositionKey = "ground.position";

    public static IReadOnlyList<GroundCopyGroup> GroundGroups { get; } =
    [
        new GroundCopyGroup
        {
            Key = GroundPositionKey,
            Label = "Map position & zoom",
            Describe = FormatGroundPosition,
            AreEqual = (a, b) => DEq(a.CenterLat, b.CenterLat) && DEq(a.CenterLon, b.CenterLon) && DEq(a.Zoom, b.Zoom) && DEq(a.Rotation, b.Rotation),
            Copy = (src, tgt) =>
            {
                tgt.CenterLat = src.CenterLat;
                tgt.CenterLon = src.CenterLon;
                tgt.Zoom = src.Zoom;
                tgt.Rotation = src.Rotation;
            },
        },
        new GroundCopyGroup
        {
            Key = "ground.lock",
            Label = "Pan/zoom lock",
            Describe = s => s.IsPanZoomLocked ? "On" : "Off",
            AreEqual = (a, b) => a.IsPanZoomLocked == b.IsPanZoomLocked,
            Copy = (src, tgt) => tgt.IsPanZoomLocked = src.IsPanZoomLocked,
        },
        new GroundCopyGroup
        {
            Key = "ground.labels",
            Label = "Runway/taxiway labels",
            Describe = s => $"Rwy {OnOff(s.ShowRunwayLabels)} · Twy {OnOff(s.ShowTaxiwayLabels)}",
            AreEqual = (a, b) => a.ShowRunwayLabels == b.ShowRunwayLabels && a.ShowTaxiwayLabels == b.ShowTaxiwayLabels,
            Copy = (src, tgt) =>
            {
                tgt.ShowRunwayLabels = src.ShowRunwayLabels;
                tgt.ShowTaxiwayLabels = src.ShowTaxiwayLabels;
            },
        },
        new GroundCopyGroup
        {
            Key = "ground.filters",
            Label = "Hold-short / parking / spot filters",
            Describe = s => $"HS:{Filter(s.ShowHoldShort)} Pk:{Filter(s.ShowParking)} Sp:{Filter(s.ShowSpot)}",
            AreEqual = (a, b) => a.ShowHoldShort == b.ShowHoldShort && a.ShowParking == b.ShowParking && a.ShowSpot == b.ShowSpot,
            Copy = (src, tgt) =>
            {
                tgt.ShowHoldShort = src.ShowHoldShort;
                tgt.ShowParking = src.ShowParking;
                tgt.ShowSpot = src.ShowSpot;
            },
        },
        new GroundCopyGroup
        {
            Key = "ground.adw",
            Label = "ADW markings",
            Describe = s => OnOff(s.ShowAdwMarkings),
            AreEqual = (a, b) => a.ShowAdwMarkings == b.ShowAdwMarkings,
            Copy = (src, tgt) => tgt.ShowAdwMarkings = src.ShowAdwMarkings,
        },
    ];

    private static string FormatGroundPosition(SavedGroundSettings s)
    {
        string zoom = $"{s.Zoom.ToString("0.##", CultureInfo.InvariantCulture)}x";
        return DEq(s.Rotation, 0) ? zoom : $"{zoom} · {Num(s.Rotation)}°";
    }

    private static string Filter(GroundFilterMode m) =>
        m switch
        {
            GroundFilterMode.LabelsAndIcons => "Labels",
            GroundFilterMode.IconsOnly => "Icons",
            GroundFilterMode.Off => "Off",
            _ => m.ToString(),
        };

    // The dock state plus, on a docked layout that saved it, the terminal's share of the tabs + terminal height. A
    // popped-out terminal is sized by its own window geometry, so its saved share never applies.
    private static string DescribeTerminal(SavedLayout layout)
    {
        if (layout.IsTerminalPoppedOut)
        {
            return "popped out";
        }

        return layout.TerminalSplitRatio is { } ratio ? $"docked · size {Num(ratio * 100)}%" : "docked";
    }

    // Two docked layouts differ in size only when both saved a share; a layout with no recorded share leaves the
    // current split as it is when applied, so it has nothing to compare.
    private static bool RatioEqual(double? a, double? b) => (a is null) || (b is null) || DEq(a.Value, b.Value);

    private static LayoutCopyGroup PopOutGroup(string key, string label, Func<SavedLayout, bool> poppedOut) =>
        new()
        {
            Key = key,
            Section = "Pop-out windows",
            Label = label,
            Describe = l => poppedOut(l) ? "popped out" : "docked",
            AreEqual = (a, b) => poppedOut(a) == poppedOut(b),
        };

    private static string FormatExtraViews(List<SavedExtraView> views) =>
        views.Count == 0 ? "none" : string.Join(", ", views.Select(v => $"#{v.Ordinal} {v.AirportId}"));

    // A Scope window has no airport, so only its number shows: "#2, #3", ascending.
    private static string FormatScopeExtras(List<SavedExtraView> views) =>
        views.Count == 0 ? "none" : string.Join(", ", ScopeOrdinals(views).Select(o => $"#{o}"));

    // A layout with no recorded Scope windows leaves them as they are when applied, so it has nothing to compare; two
    // that recorded them differ only in which numbers are open.
    private static bool ScopeExtrasEqual(List<SavedExtraView>? a, List<SavedExtraView>? b) =>
        (a is null) || (b is null) || ScopeOrdinals(a).SequenceEqual(ScopeOrdinals(b));

    // A number listed twice is one window, as ReconcileExtraScopeViews opens it once.
    private static IEnumerable<int> ScopeOrdinals(List<SavedExtraView> views) => views.Select(v => v.Ordinal).Distinct().Order();

    private static string FormatOpenTabs(SavedOpenTabs tabs)
    {
        if ((tabs.Strips.Count == 0) && (tabs.Tdls.Count == 0))
        {
            return "none";
        }

        List<string> parts = [];
        if (tabs.Strips.Count > 0)
        {
            parts.Add($"Strips: {string.Join(", ", tabs.Strips)}");
        }

        if (tabs.Tdls.Count > 0)
        {
            parts.Add($"vTDLS: {string.Join(", ", tabs.Tdls)}");
        }

        return string.Join(" · ", parts);
    }

    private static bool OpenTabsEqual(SavedOpenTabs? a, SavedOpenTabs? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return SequenceEqual(a.Strips, b.Strips) && SequenceEqual(a.Tdls, b.Tdls);
    }

    private static string FormatGrid(SavedGridLayout? layout)
    {
        if (layout is null)
        {
            return "default";
        }

        int columns = layout.ColumnOrder?.Count ?? 0;
        int hidden = layout.HiddenColumns?.Count ?? 0;
        return ((columns == 0) && (hidden == 0)) ? "custom" : $"{columns} cols, {hidden} hidden";
    }

    private static bool GridEqual(SavedGridLayout? a, SavedGridLayout? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return SequenceEqual(a.ColumnOrder, b.ColumnOrder)
            && SequenceEqual(a.HiddenColumns, b.HiddenColumns)
            && (a.SortColumn == b.SortColumn)
            && (a.SortDirection == b.SortDirection)
            && WidthsEqual(a.ColumnWidths, b.ColumnWidths);
    }

    private static bool SequenceEqual(List<string>? a, List<string>? b) => (a ?? []).SequenceEqual(b ?? []);

    private static bool WidthsEqual(Dictionary<string, double>? a, Dictionary<string, double>? b)
    {
        int countA = a?.Count ?? 0;
        int countB = b?.Count ?? 0;
        if (countA != countB)
        {
            return false;
        }

        if (a is null || b is null)
        {
            return true;
        }

        foreach ((string? key, double value) in a)
        {
            if (!b.TryGetValue(key, out double other) || Math.Abs(other - value) > 0.5)
            {
                return false;
            }
        }

        return true;
    }

    private static bool DEq(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string Plural(int count) => count == 1 ? "" : "s";

    private static string Num(double value) => value.ToString("0", CultureInfo.InvariantCulture);
}
