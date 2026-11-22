using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport.Fillet;

/// <summary>
/// Matches each per-corner fillet override to the corner of the pre-fillet layout it names: a junction within
/// <see cref="ApexMatchToleranceFt"/> of its apex, with an arm pair carrying its two taxiway names (trimmed,
/// case-insensitive, either order) at bearings each within <see cref="ArmBearingToleranceDeg"/> of the override's.
/// Every override is resolved against every corner, never first-hit in entry order, so the result does not depend on the
/// sidecar's load order: an override matching more than one corner, or a corner named by more than one override, is
/// ambiguous and an override matching none is orphaned, and neither is applied anywhere.
/// </summary>
internal static class FilletOverrideResolver
{
    private static readonly ILogger Log = SimLog.CreateLogger("FilletOverrideResolver");

    /// <summary>Apex match radius, feet: a judgement call, about GeoJsonParser's SnapToleranceDeg (~10 ft).</summary>
    internal const double ApexMatchToleranceFt = 10.0;

    /// <summary>Arm bearing match tolerance, degrees: a judgement call; 15° separates the arms of a real junction.</summary>
    internal const double ArmBearingToleranceDeg = 15.0;

    /// <summary>One corner the corner loop would visit: a junction and the ids of its two arms.</summary>
    private readonly record struct Corner(int JunctionNodeId, int ArmIdA, int ArmIdB);

    /// <summary>Resolves every entry of <paramref name="overrides"/> against <paramref name="layout"/>, before it is filleted.</summary>
    /// <param name="layout">The pre-fillet layout, with its adjacency lists built.</param>
    /// <param name="manualArcNodes">The layout's hand-drawn arc shape points, as the corner loop walks arms with them.</param>
    /// <param name="overrides">The airport's overrides.</param>
    /// <returns>One row per override, in entry order.</returns>
    public static IReadOnlyList<FilletOverrideReport> Resolve(
        AirportGroundLayout layout,
        HashSet<int> manualArcNodes,
        CornerFilletOverrides overrides
    )
    {
        if (overrides.Entries.Count == 0)
        {
            return [];
        }

        List<GroundNode> junctions =
        [
            .. layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.TaxiwayIntersection) && (n.Edges.Count >= 2)).OrderBy(n => n.Id),
        ];
        var armsByNode = new Dictionary<int, IReadOnlyList<TaxiwayArm>>();
        List<(CornerFilletOverride Override, List<Corner> Corners)> matches =
        [
            .. overrides.Entries.Select(o => (o, CornersMatching(o, junctions, manualArcNodes, armsByNode))),
        ];
        var overridesPerCorner = matches.Where(m => m.Corners.Count == 1).GroupBy(m => m.Corners[0]).ToDictionary(g => g.Key, g => g.Count());

        List<FilletOverrideReport> rows = [.. matches.Select(m => ReportFor(m.Override, m.Corners, overridesPerCorner))];
        foreach (FilletOverrideReport row in rows.Where(r => r.Match != FilletOverrideMatch.Matched))
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug(
                    "Corner fillet override {A}/{B} at ({Lat}, {Lon}): {Match}, {Detail}",
                    row.Override.TaxiwayA,
                    row.Override.TaxiwayB,
                    row.Override.ApexLat,
                    row.Override.ApexLon,
                    row.Match,
                    row.Detail
                );
            }
        }

        return rows;
    }

    private static FilletOverrideReport ReportFor(CornerFilletOverride entry, List<Corner> corners, Dictionary<Corner, int> overridesPerCorner)
    {
        if (corners.Count == 0)
        {
            string detail = $"no corner within {ApexMatchToleranceFt:0} ft with taxiways {entry.TaxiwayA}/{entry.TaxiwayB} and matching arm bearings";
            return new FilletOverrideReport(entry, FilletOverrideMatch.Orphaned, null, detail);
        }

        if (corners.Count > 1)
        {
            return new FilletOverrideReport(entry, FilletOverrideMatch.Ambiguous, null, $"{corners.Count} corners match");
        }

        Corner corner = corners[0];
        int named = overridesPerCorner[corner];
        return (named > 1)
            ? new FilletOverrideReport(entry, FilletOverrideMatch.Ambiguous, corner.JunctionNodeId, $"{named} overrides name this corner")
            : new FilletOverrideReport(entry, FilletOverrideMatch.Matched, corner.JunctionNodeId, $"matches junction {corner.JunctionNodeId}");
    }

    private static List<Corner> CornersMatching(
        CornerFilletOverride entry,
        List<GroundNode> junctions,
        HashSet<int> manualArcNodes,
        Dictionary<int, IReadOnlyList<TaxiwayArm>> armsByNode
    )
    {
        var apex = new LatLon(entry.ApexLat, entry.ApexLon);
        var corners = new List<Corner>();
        foreach (GroundNode node in junctions)
        {
            if ((GeoMath.DistanceNm(apex, node.Position) * GeoMath.FeetPerNm) > ApexMatchToleranceFt)
            {
                continue;
            }

            if (!armsByNode.TryGetValue(node.Id, out IReadOnlyList<TaxiwayArm>? arms))
            {
                arms = TaxiwayArmBuilder.BuildArms(node, manualArcNodes);
                armsByNode[node.Id] = arms;
            }

            for (int i = 0; i < arms.Count; i++)
            {
                for (int j = i + 1; j < arms.Count; j++)
                {
                    if (PairMatches(entry, arms[i], arms[j]))
                    {
                        corners.Add(new Corner(node.Id, arms[i].Id, arms[j].Id));
                    }
                }
            }
        }

        return corners;
    }

    private static bool PairMatches(CornerFilletOverride entry, TaxiwayArm first, TaxiwayArm second) =>
        (ArmMatches(entry.TaxiwayA, entry.BearingA, first) && ArmMatches(entry.TaxiwayB, entry.BearingB, second))
        || (ArmMatches(entry.TaxiwayA, entry.BearingA, second) && ArmMatches(entry.TaxiwayB, entry.BearingB, first));

    private static bool ArmMatches(string taxiway, double bearingDeg, TaxiwayArm arm) =>
        string.Equals(taxiway.Trim(), arm.TaxiwayName.Trim(), StringComparison.OrdinalIgnoreCase)
        && (BearingDifferenceDeg(bearingDeg, arm.BearingFromJunctionDeg) <= ArmBearingToleranceDeg);

    private static double BearingDifferenceDeg(double first, double second) => Math.Abs(((((first - second) % 360.0) + 540.0) % 360.0) - 180.0);
}
