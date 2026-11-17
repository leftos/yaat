using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope.Prefs;

/// <summary>
/// The Scope's map-tool settings as a pref set stores them; mva and lock are required, so a file missing either is skipped as
/// unreadable, and the range ring's, weather's, fixes and markers' and the deconfliction mode's defaults let a file written
/// without them load.
/// </summary>
/// <param name="Mva">Whether the MVA altitude tint is on.</param>
/// <param name="Lock">Whether pan and zoom are locked.</param>
/// <param name="RangeRing">
/// The range ring, or null for the default: on, 5 NM apart, centred on the facility's home centre. A set from an older
/// build has none.
/// </param>
/// <param name="Weather">Whether the weather overlay is on, or null for on, the default; a set from an older build has none.</param>
/// <param name="FixesAndMarkers">
/// The FIX overlay and the pinned markers, or null for the default: FIX off and no marker. A set from an older build has none.
/// </param>
public sealed record ScopePrefMapTools(
    bool Mva,
    bool Lock,
    ScopePrefRangeRing? RangeRing = null,
    bool? Weather = null,
    ScopePrefFixesAndMarkers? FixesAndMarkers = null
)
{
    /// <summary>
    /// The datablock deconfliction mode, or null for the mode a new Scope opens with (the Radar's,
    /// <c>UserPreferences.RadarDeconflictMode</c>). A set from an older build has none.
    /// </summary>
    public DatablockDeconflictMode? Deconflict { get; init; }
}

/// <summary>
/// The Scope's range ring as a pref set stores it; all four keys are required (an unplaced centre is written as null), so a file
/// missing one is skipped as unreadable.
/// </summary>
/// <param name="Show">Whether the ring is on.</param>
/// <param name="SizeNm">The spacing of its circles, in NM: 2, 5, 10, 15 or 20.</param>
/// <param name="CenterLat">The latitude of the centre PLACE RR placed, or null for the facility's home centre.</param>
/// <param name="CenterLon">The longitude of the centre PLACE RR placed, or null for the facility's home centre.</param>
public sealed record ScopePrefRangeRing(bool Show, int SizeNm, double? CenterLat, double? CenterLon);

/// <summary>
/// The Scope's FIX overlay and pinned markers as a pref set stores them; both keys are required, so a file missing one is
/// skipped as unreadable.
/// </summary>
/// <param name="ShowFixes">Whether the FIX overlay is on.</param>
/// <param name="Markers">The pinned markers' fix names or FRDs, upper-cased, in the order they were pinned.</param>
public sealed record ScopePrefFixesAndMarkers(bool ShowFixes, IReadOnlyList<string> Markers);
