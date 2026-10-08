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
}
