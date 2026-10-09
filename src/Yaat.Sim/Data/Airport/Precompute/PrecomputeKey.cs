using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// Identifies one airport's precomputed data, so a stored entry is recognised as stale. <see cref="LayoutMatches"/>
/// covers everything the layout payload depends on; <see cref="PushTargetsMatch"/> adds the push-target sources and the
/// airport's sidecar hash, so a precompute-planner-only or sidecar-only change invalidates the push half alone. The tug
/// planner's files (<c>TugMovePlanner</c>, <c>TugPathCheck</c> and the rest under <c>Data/Airport</c>) are in both source
/// sets, so an edit to one stales both halves.
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
    /// The key of data computed now from the offline facts alone, for comparison through
    /// <see cref="PushTargetsMatchOffline"/>: its four offline fields (the layout format version and the three source
    /// hashes) are current, its <see cref="GeoJsonMd5"/> is empty and its <see cref="NavDataSerial"/> zero, so
    /// <see cref="LayoutMatches"/> and <see cref="PushTargetsMatch"/> must not be used to compare it.
    /// </summary>
    /// <param name="pushSidecarHash">The airport's sidecar hash (<see cref="AirportSidecarHash.For"/>).</param>
    /// <returns>The key.</returns>
    public static PrecomputeKey CurrentOffline(string pushSidecarHash) => Current(string.Empty, 0, pushSidecarHash);

    /// <summary>
    /// The key of data computed now, from the airport's GeoJSON hash, the loaded navdata serial and the airport's sidecar
    /// hash (<see cref="AirportSidecarHash.For"/>).
    /// </summary>
    public static PrecomputeKey Current(string geoJsonMd5, long navDataSerial, string pushSidecarHash) =>
        new()
        {
            GeoJsonMd5 = geoJsonMd5,
            NavDataSerial = navDataSerial,
            LayoutFormatVersion = RecordingArchive.CurrentLayoutFormatVersion,
            LayoutSourceHash = PrecomputeSourceHashes.Layout,
            PushTargetSourceHash = PrecomputeSourceHashes.PushTargets,
            PushSidecarHash = pushSidecarHash,
        };

    /// <summary>True when the layout payload computed under <paramref name="other"/> is still current.</summary>
    public bool LayoutMatches(PrecomputeKey other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return (GeoJsonMd5 == other.GeoJsonMd5)
            && (NavDataSerial == other.NavDataSerial)
            && (LayoutFormatVersion == other.LayoutFormatVersion)
            && (LayoutSourceHash == other.LayoutSourceHash);
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
