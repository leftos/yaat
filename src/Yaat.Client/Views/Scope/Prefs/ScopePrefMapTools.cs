using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope.Prefs;

/// <summary>
/// The Scope's map-tool settings as a pref set stores them; the constructor's keys are required, so a file missing one is
/// skipped as unreadable, and deconflict may be left out. A null range ring, weather switch or fixes-and-markers entry is
/// the default, which is how a default is written: the ring on at 5 NM on the facility's home centre, WX on, and FIX off
/// with no marker.
/// </summary>
/// <param name="Mva">Whether the MVA altitude tint is on.</param>
/// <param name="Lock">Whether pan and zoom are locked.</param>
/// <param name="RangeRing">The range ring, or null for the default: on, 5 NM apart, centred on the facility's home centre.</param>
/// <param name="Weather">Whether the weather overlay is on, or null for on, the default.</param>
/// <param name="FixesAndMarkers">The FIX overlay and the pinned markers, or null for the default: FIX off and no marker.</param>
public sealed record ScopePrefMapTools(bool Mva, bool Lock, ScopePrefRangeRing? RangeRing, bool? Weather, ScopePrefFixesAndMarkers? FixesAndMarkers)
{
    /// <summary>
    /// The datablock deconfliction mode, or null for the mode a new Scope opens with (the Radar's,
    /// <c>UserPreferences.RadarDeconflictMode</c>).
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
