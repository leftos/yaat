namespace Yaat.Sim.Asdex;

/// <summary>
/// The airport's sidecar <c>asdexLandingTaxiways</c> list applied to the ground layout's taxiway segments: a null set is no list
/// authored, so every taxiway alerts; an empty set is a list naming none, so no segment is left to alert on.
/// </summary>
public static class AsdexConfiguredTaxiways
{
    /// <summary>
    /// <paramref name="segments"/> limited to the taxiways <paramref name="configured"/> names, or uncopied and unfiltered when it
    /// is null. The set is expected to compare case-insensitively, which is what the loader and the catalog build.
    /// </summary>
    public static IReadOnlyList<AsdexTaxiwaySegment> Filter(IReadOnlyList<AsdexTaxiwaySegment> segments, IReadOnlySet<string>? configured)
    {
        if (configured is null)
        {
            return segments;
        }

        return [.. segments.Where(segment => configured.Contains(segment.Name))];
    }
}
