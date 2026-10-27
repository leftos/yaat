using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// Identifies one airport's precomputed data, so a stored entry is recognised as stale. <see cref="LayoutMatches"/> covers
/// everything the layout payload depends on, the airport's stand lead-ins among them (the one sidecar section the layout
/// build reads), its tower-cab videomap and the runway inputs its hold-short standoff rule reads;
/// <see cref="PushTargetsMatch"/> adds the push-target sources and the airport's whole sidecar hash, so a
/// precompute-planner-only change, or a sidecar change outside the lead-ins, invalidates the push half alone. The tug
/// planner's files (the <c>Tug*.cs</c> files under <c>Data/Airport</c>) are in the push-target set only, so an edit to one
/// stales only the push half.
/// </summary>
public sealed record PrecomputeKey
{
    /// <summary>The MD5 of the airport's GeoJSON.</summary>
    public required string GeoJsonMd5 { get; init; }

    /// <summary>The serial of the navdata the layout was built against.</summary>
    public required long NavDataSerial { get; init; }

    /// <summary>The layout serialization format version.</summary>
    public required int LayoutFormatVersion { get; init; }

    /// <summary>The hash of the layout source set.</summary>
    public required string LayoutSourceHash { get; init; }

    /// <summary>The hash of the push-target source set.</summary>
    public required string PushTargetSourceHash { get; init; }

    /// <summary>The hash of the airport's sidecars (<see cref="AirportSidecarHash.For"/>).</summary>
    public required string PushSidecarHash { get; init; }

    /// <summary>
    /// The hash of the airport's stand lead-ins (<see cref="AirportSidecarHash.ForStandLeadIns"/>). Not required, so an entry
    /// written before the field existed still reads, with an empty hash that matches no current key.
    /// </summary>
    public string StandLeadInHash { get; init; } = string.Empty;

    /// <summary>
    /// The hash of the airport's tower-cab videomap (<see cref="TowerCabVideoMap.Hash"/>), empty when it has none. Not
    /// required, so an entry written before the field existed still reads, with the empty hash of an airport without a map.
    /// </summary>
    public string VideoMapHash { get; init; } = string.Empty;

    /// <summary>
    /// The hash of the airport's runway inputs the hold-short standoff rule reads (<see cref="HoldShortStandoff.InputHash"/>),
    /// empty when no navdata was loaded. Not required, so an entry written before the field existed still reads, with an
    /// empty hash that matches no key computed with navdata.
    /// </summary>
    public string StandoffInputHash { get; init; } = string.Empty;

    /// <summary>
    /// The key of data computed now from the offline facts alone, for comparison through
    /// <see cref="PushTargetsMatchOffline"/>: its four offline fields (the layout format version and the three source
    /// hashes) are current, its <see cref="GeoJsonMd5"/>, <see cref="StandLeadInHash"/>, <see cref="VideoMapHash"/> and
    /// <see cref="StandoffInputHash"/> are empty and its <see cref="NavDataSerial"/> zero, so <see cref="LayoutMatches"/>
    /// and <see cref="PushTargetsMatch"/> must not be used to compare it. The sidecar hash covers the lead-ins, so an
    /// offline comparison still sees a lead-in edit.
    /// </summary>
    /// <param name="pushSidecarHash">The airport's sidecar hash (<see cref="AirportSidecarHash.For"/>).</param>
    /// <returns>The key.</returns>
    public static PrecomputeKey CurrentOffline(string pushSidecarHash) =>
        Current(string.Empty, 0, pushSidecarHash, string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// The key of data computed now, from the airport's GeoJSON hash, the loaded navdata serial, the airport's sidecar hash
    /// (<see cref="AirportSidecarHash.For"/>), its stand lead-in hash (<see cref="AirportSidecarHash.ForStandLeadIns"/>),
    /// its tower-cab videomap hash (<see cref="TowerCabVideoMap.Hash"/>, empty when it has none) and its standoff-input
    /// hash (<see cref="HoldShortStandoff.InputHash"/>).
    /// </summary>
    public static PrecomputeKey Current(
        string geoJsonMd5,
        long navDataSerial,
        string pushSidecarHash,
        string standLeadInHash,
        string videoMapHash,
        string standoffInputHash
    ) =>
        new()
        {
            GeoJsonMd5 = geoJsonMd5,
            NavDataSerial = navDataSerial,
            LayoutFormatVersion = RecordingArchive.CurrentLayoutFormatVersion,
            LayoutSourceHash = PrecomputeSourceHashes.Layout,
            PushTargetSourceHash = PrecomputeSourceHashes.PushTargets,
            PushSidecarHash = pushSidecarHash,
            StandLeadInHash = standLeadInHash,
            VideoMapHash = videoMapHash,
            StandoffInputHash = standoffInputHash,
        };

    /// <summary>True when the layout payload computed under <paramref name="other"/> is still current.</summary>
    public bool LayoutMatches(PrecomputeKey other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return (GeoJsonMd5 == other.GeoJsonMd5)
            && (NavDataSerial == other.NavDataSerial)
            && (LayoutFormatVersion == other.LayoutFormatVersion)
            && (LayoutSourceHash == other.LayoutSourceHash)
            && (StandLeadInHash == other.StandLeadInHash)
            && (VideoMapHash == other.VideoMapHash)
            && (StandoffInputHash == other.StandoffInputHash);
    }

    /// <summary>True when the push targets computed under <paramref name="other"/> are still current.</summary>
    public bool PushTargetsMatch(PrecomputeKey other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return (LayoutMatches(other)) && (PushTargetSourceHash == other.PushTargetSourceHash) && (PushSidecarHash == other.PushSidecarHash);
    }

    /// <summary>
    /// True when the push targets computed under <paramref name="other"/> are still current to a reader that has no
    /// vNAS: the layout format version and the three source hashes all match. The GeoJSON MD5 and the navdata serial are
    /// online facts only the maintainer's run knows, so they are not compared; compare through
    /// <see cref="CurrentOffline"/>, which leaves them blank.
    /// </summary>
    /// <param name="other">The key to compare with, as a rule <see cref="CurrentOffline"/> made.</param>
    /// <returns>True when the push targets are current offline.</returns>
    public bool PushTargetsMatchOffline(PrecomputeKey other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return (LayoutFormatVersion == other.LayoutFormatVersion)
            && (LayoutSourceHash == other.LayoutSourceHash)
            && (PushTargetSourceHash == other.PushTargetSourceHash)
            && (PushSidecarHash == other.PushSidecarHash);
    }
}
