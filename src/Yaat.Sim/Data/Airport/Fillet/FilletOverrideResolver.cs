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

    /// <summary>
    /// Resolves every entry of <paramref name="overrides"/> against <paramref name="layout"/>, before it is filleted, keeping
    /// the corner each matched override names.
    /// </summary>
    /// <param name="layout">The pre-fillet layout, with its adjacency lists built.</param>
    /// <param name="manualArcNodes">The layout's hand-drawn arc shape points, as the corner loop walks arms with them.</param>
    /// <param name="overrides">The airport's overrides.</param>
    /// <returns>One entry per override, in entry order.</returns>
    public static IReadOnlyList<ResolvedOverride> ResolveCorners(
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
        List<(CornerFilletOverride Override, List<ResolvedCorner> Corners)> matches =
        [
            .. overrides.Entries.Select(o => (o, CornersMatching(o, junctions, manualArcNodes, armsByNode))),
        ];
        var overridesPerCorner = matches.Where(m => m.Corners.Count == 1).GroupBy(m => m.Corners[0]).ToDictionary(g => g.Key, g => g.Count());

        List<ResolvedOverride> resolved = [.. matches.Select(m => ToResolved(m.Override, m.Corners, overridesPerCorner))];
        foreach (FilletOverrideReport row in resolved.Select(r => r.Row).Where(r => r.Match != FilletOverrideMatch.Matched))
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

        return resolved;
    }

    private static ResolvedOverride ToResolved(
        CornerFilletOverride entry,
        List<ResolvedCorner> corners,
        Dictionary<ResolvedCorner, int> overridesPerCorner
    )
    {
        FilletOverrideReport row = ReportFor(entry, corners, overridesPerCorner);
        return new ResolvedOverride(row, (row.Match == FilletOverrideMatch.Matched) ? corners[0] : null);
    }

    private static FilletOverrideReport ReportFor(
        CornerFilletOverride entry,
        List<ResolvedCorner> corners,
        Dictionary<ResolvedCorner, int> overridesPerCorner
    )
    {
        if (corners.Count == 0)
        {
            string detail = $"no corner within {ApexMatchToleranceFt:0} ft with taxiways {entry.TaxiwayA}/{entry.TaxiwayB} and matching arm bearings";
            return new FilletOverrideReport(entry, FilletOverrideMatch.Orphaned, null, detail, null, null, null);
        }

        if (corners.Count > 1)
        {
            return new FilletOverrideReport(entry, FilletOverrideMatch.Ambiguous, null, $"{corners.Count} corners match", null, null, null);
        }

        ResolvedCorner corner = corners[0];
        int named = overridesPerCorner[corner];
        return (named > 1)
            ? new FilletOverrideReport(
                entry,
                FilletOverrideMatch.Ambiguous,
                corner.JunctionNodeId,
                $"{named} overrides name this corner",
                null,
                null,
                null
            )
            : new FilletOverrideReport(
                entry,
                FilletOverrideMatch.Matched,
                corner.JunctionNodeId,
                $"matches junction {corner.JunctionNodeId}",
                null,
                null,
                null
            );
    }

    private static List<ResolvedCorner> CornersMatching(
        CornerFilletOverride entry,
        List<GroundNode> junctions,
        HashSet<int> manualArcNodes,
        Dictionary<int, IReadOnlyList<TaxiwayArm>> armsByNode
    )
    {
        var apex = new LatLon(entry.ApexLat, entry.ApexLon);
        var corners = new List<ResolvedCorner>();
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
                        corners.Add(new ResolvedCorner(node.Id, arms[i].Id, arms[j].Id));
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

/// <summary>
/// One corner the corner loop would visit: a junction and the ids of its two arms (<see cref="TaxiwayArm.Id"/>), the lower
/// id first, as the planner pairs them.
/// </summary>
internal readonly record struct ResolvedCorner(int JunctionNodeId, int ArmIdA, int ArmIdB);

/// <summary>An override's report row and, when it matched, the corner it names.</summary>
internal readonly record struct ResolvedOverride(FilletOverrideReport Row, ResolvedCorner? Corner);

/// <summary>
/// The matched corner fillet overrides of one pass, by the corner each names, as the corner loop and the planner apply
/// them. Immutable; lookups only, so no iteration order decides geometry.
/// </summary>
internal sealed class FilletCornerEdits
{
    private readonly Dictionary<ResolvedCorner, CornerFilletOverride> _byCorner;
    private readonly HashSet<int> _forcedJunctions;

    private FilletCornerEdits(Dictionary<ResolvedCorner, CornerFilletOverride> byCorner)
    {
        _byCorner = byCorner;
        _forcedJunctions = [.. byCorner.Where(kv => kv.Value.Force).Select(kv => kv.Key.JunctionNodeId)];
    }

    /// <summary>The forced corners, by junction node id and then arm ids.</summary>
    public IReadOnlyList<ResolvedCorner> ForcedCorners =>
        [.. _byCorner.Where(kv => kv.Value.Force).Select(kv => kv.Key).OrderBy(c => c.JunctionNodeId).ThenBy(c => c.ArmIdA).ThenBy(c => c.ArmIdB)];

    /// <summary>The edits of every matched override in <paramref name="resolved"/>; each corner is named by one at most.</summary>
    public static FilletCornerEdits From(IReadOnlyList<ResolvedOverride> resolved)
    {
        var byCorner = new Dictionary<ResolvedCorner, CornerFilletOverride>();
        foreach (ResolvedOverride entry in resolved)
        {
            if (entry.Corner is { } corner)
            {
                byCorner.Add(corner, entry.Row.Override);
            }
        }

        return new(byCorner);
    }

    /// <summary>True when an override forces a corner of junction <paramref name="junctionNodeId"/>.</summary>
    public bool ForcesAt(int junctionNodeId) => _forcedJunctions.Contains(junctionNodeId);

    /// <summary>The override naming the arm pair of junction <paramref name="junctionNodeId"/>, or null.</summary>
    public CornerFilletOverride? At(int junctionNodeId, int armIdA, int armIdB) =>
        _byCorner.GetValueOrDefault(new ResolvedCorner(junctionNodeId, Math.Min(armIdA, armIdB), Math.Max(armIdA, armIdB)));

    /// <summary>These edits with the force of every corner in <paramref name="corners"/> withdrawn.</summary>
    public FilletCornerEdits WithoutForce(IReadOnlyCollection<ResolvedCorner> corners) =>
        new(_byCorner.ToDictionary(kv => kv.Key, kv => corners.Contains(kv.Key) ? (kv.Value with { Force = false }) : kv.Value));
}
