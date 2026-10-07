using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// Identifies one airport's precomputed data, so a stored entry is recognised as stale. <see cref="LayoutMatches"/>
/// covers everything the layout payload depends on; <see cref="PushTargetsMatch"/> adds the push-target sources, so a
/// planner-only change invalidates the push half alone.
/// </summary>
public sealed record PrecomputeKey(
    string GeoJsonMd5,
    long NavDataSerial,
    int LayoutFormatVersion,
    string LayoutSourceHash,
    string PushTargetSourceHash
)
{
    /// <summary>The key of data computed now, from the airport's GeoJSON hash and the loaded navdata serial.</summary>
    public static PrecomputeKey Current(string geoJsonMd5, long navDataSerial) =>
        new(
            geoJsonMd5,
            navDataSerial,
            RecordingArchive.CurrentLayoutFormatVersion,
            PrecomputeSourceHashes.Layout,
            PrecomputeSourceHashes.PushTargets
        );

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
        return (LayoutMatches(other)) && (PushTargetSourceHash == other.PushTargetSourceHash);
    }
}
