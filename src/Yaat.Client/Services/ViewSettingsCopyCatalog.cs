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

/// <summary>One selectable section of per-scenario Radar view settings. See <see cref="GroundCopyGroup"/>.</summary>
public sealed class RadarCopyGroup
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required Func<SavedRadarSettings, string> Describe { get; init; }
    public required Func<SavedRadarSettings, SavedRadarSettings, bool> AreEqual { get; init; }
    public required Action<SavedRadarSettings, SavedRadarSettings> Copy { get; init; }
}

/// <summary>
/// Single source of truth for how per-scenario Ground/Radar view settings are grouped when
/// copying from one scenario to another. Used by both <c>CopyViewSettingsDialog</c> (to render
/// the comparison rows) and the apply step in <c>MainWindow</c> (to merge the selected sections
/// into the current settings), so the groupings can never drift apart.
/// </summary>
public static class ViewSettingsCopyCatalog
{
    /// <summary>Position group keys carry an airport-aware label and the cross-airport warning.</summary>
    public const string GroundPositionKey = "ground.position";

    public const string RadarCenterKey = "radar.center";
    public const string RadarMapsKey = "radar.maps";

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
    ];

    public static IReadOnlyList<RadarCopyGroup> RadarGroups { get; } =
    [
        new RadarCopyGroup
        {
            Key = RadarMapsKey,
            Label = "Video maps (selected)",
            Describe = s => s.EnabledStarsIds.Count == 0 ? "none" : $"{s.EnabledStarsIds.Count} map{Plural(s.EnabledStarsIds.Count)}",
            AreEqual = (a, b) => new HashSet<int>(a.EnabledStarsIds).SetEquals(b.EnabledStarsIds),
            Copy = (src, tgt) => tgt.EnabledStarsIds = [.. src.EnabledStarsIds],
        },
        new RadarCopyGroup
        {
            Key = RadarCenterKey,
            Label = "Center & range",
            Describe = s => $"{Num(s.RangeNm)} nm",
            AreEqual = (a, b) => DEq(a.CenterLat, b.CenterLat) && DEq(a.CenterLon, b.CenterLon) && DEq(a.RangeNm, b.RangeNm),
            Copy = (src, tgt) =>
            {
                tgt.CenterLat = src.CenterLat;
                tgt.CenterLon = src.CenterLon;
                tgt.RangeNm = src.RangeNm;
            },
        },
        new RadarCopyGroup
        {
            Key = "radar.rings",
            Label = "Range rings",
            Describe = s => s.ShowRangeRings ? $"on · {Num(s.RangeRingSizeNm)} nm" : "off",
            AreEqual = (a, b) =>
                a.ShowRangeRings == b.ShowRangeRings
                && DEq(a.RangeRingSizeNm, b.RangeRingSizeNm)
                && DEq(a.RangeRingCenterLat, b.RangeRingCenterLat)
                && DEq(a.RangeRingCenterLon, b.RangeRingCenterLon),
            Copy = (src, tgt) =>
            {
                tgt.ShowRangeRings = src.ShowRangeRings;
                tgt.RangeRingSizeNm = src.RangeRingSizeNm;
                tgt.RangeRingCenterLat = src.RangeRingCenterLat;
                tgt.RangeRingCenterLon = src.RangeRingCenterLon;
            },
        },
        new RadarCopyGroup
        {
            Key = "radar.ptl",
            Label = "PTL (predicted track line)",
            Describe = s =>
                ((!s.PtlOwn && !s.PtlAll) || s.PtlLengthMinutes <= 0)
                    ? "off"
                    : $"{s.PtlLengthMinutes.ToString("0.0", CultureInfo.InvariantCulture)} min · {(s.PtlAll ? "all" : "own")}",
            AreEqual = (a, b) => DEq(a.PtlLengthMinutes, b.PtlLengthMinutes) && a.PtlOwn == b.PtlOwn && a.PtlAll == b.PtlAll,
            Copy = (src, tgt) =>
            {
                tgt.PtlLengthMinutes = src.PtlLengthMinutes;
                tgt.PtlOwn = src.PtlOwn;
                tgt.PtlAll = src.PtlAll;
            },
        },
        new RadarCopyGroup
        {
            Key = "radar.brightness",
            Label = "Brightness levels",
            Describe = s => s.BrightnessValues is { Count: > 0 } b ? $"{b.Count} levels" : "default",
            AreEqual = (a, b) => BrightnessEqual(a.BrightnessValues, b.BrightnessValues),
            Copy = (src, tgt) => tgt.BrightnessValues = src.BrightnessValues is null ? null : new Dictionary<string, int>(src.BrightnessValues),
        },
        new RadarCopyGroup
        {
            Key = "radar.fixestopdown",
            Label = "Fixes / top-down",
            Describe = s => $"Fixes {OnOff(s.ShowFixes)} · TD {OnOff(s.ShowTopDown)}",
            AreEqual = (a, b) => a.ShowFixes == b.ShowFixes && a.ShowTopDown == b.ShowTopDown,
            Copy = (src, tgt) =>
            {
                tgt.ShowFixes = src.ShowFixes;
                tgt.ShowTopDown = src.ShowTopDown;
            },
        },
        new RadarCopyGroup
        {
            Key = "radar.lock",
            Label = "Pan/zoom lock",
            Describe = s => s.IsPanZoomLocked ? "On" : "Off",
            AreEqual = (a, b) => a.IsPanZoomLocked == b.IsPanZoomLocked,
            Copy = (src, tgt) => tgt.IsPanZoomLocked = src.IsPanZoomLocked,
        },
        new RadarCopyGroup
        {
            Key = "radar.history",
            Label = "History trail",
            Describe = s => s.HistoryCount.ToString(CultureInfo.InvariantCulture),
            AreEqual = (a, b) => a.HistoryCount == b.HistoryCount,
            Copy = (src, tgt) => tgt.HistoryCount = src.HistoryCount,
        },
    ];

    private static string FormatGroundPosition(SavedGroundSettings s)
    {
        var zoom = $"{s.Zoom.ToString("0.##", CultureInfo.InvariantCulture)}x";
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

    private static bool BrightnessEqual(Dictionary<string, int>? a, Dictionary<string, int>? b)
    {
        var countA = a?.Count ?? 0;
        var countB = b?.Count ?? 0;
        if (countA != countB)
        {
            return false;
        }

        if (a is null || b is null)
        {
            return true;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || other != value)
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
