using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Classifies a runway hold-short node as a full-length entrance or an intersection departure for a given
/// departing runway end, and names the taxiway an intersection sits on. Display only — nothing here gates
/// clearances, routing, or physics.
///
/// The hold short nearest the departing end — by along-track distance along the runway pavement centerline —
/// is the full-length entrance. A runway end is usually reachable from both sides, so a second hold short on
/// the *opposite* side of the runway is the same entrance and is full length too, when it is either
/// alongside the first (within <see cref="OppositeSideBandFt"/>) or on the same taxiway (a taxiway crossing
/// the end at an angle puts its two bars several hundred feet apart along-track). Two hold shorts on the
/// *same* side are always two different entrances — only the nearer one is full length; the other is an
/// intersection and gets named, however close to the end it sits.
/// </summary>
public static class RunwayEntryPoint
{
    private static readonly ILogger Log = SimLog.CreateLogger("RunwayEntryPoint");

    /// <summary>
    /// How far past the full-length hold short an opposite-side hold short may sit and still be the same
    /// entrance (feet). Measured across the committed layouts, the widest genuine opposite-side pair on
    /// different taxiways is 96 ft (KOAK 15: F at 27 ft, D at 123 ft), while the closest opposite-side pair
    /// that is really an intersection is 188 ft (KMIA 08R: L1 at 29 ft, M1 at 217 ft). 150 ft sits between.
    /// Pairs on the same taxiway skip this gate entirely — they are one crossing, not two entrances.
    /// </summary>
    public const double OppositeSideBandFt = 150;

    /// <summary>
    /// The taxiway name of an intersection-departure entry point (e.g. "E" for KOAK 28R at Echo), or null
    /// when <paramref name="holdShortNodeId"/> is the runway's full-length entrance — or when the runway
    /// geometry or taxiway name cannot be resolved, in which case callers fall back to the bare runway.
    /// </summary>
    /// <param name="layout">Ground layout owning the hold-short node.</param>
    /// <param name="holdShortNodeId">Hold-short node the aircraft is queued at.</param>
    /// <param name="runwayDesignator">The end being departed (e.g. "28R"); the reciprocal end classifies the same nodes differently.</param>
    /// <param name="currentTaxiway">The aircraft's current taxiway, used only to disambiguate a hold short that carries more than one taxiway name. Null when unknown.</param>
    public static string? Resolve(AirportGroundLayout layout, int holdShortNodeId, string runwayDesignator, string? currentTaxiway)
    {
        if (!layout.Nodes.TryGetValue(holdShortNodeId, out var node))
        {
            return null;
        }

        if (!TryBuildAxis(layout, runwayDesignator, out var threshold, out var heading))
        {
            return null;
        }

        var nearest = FindFullLengthHoldShort(layout, runwayDesignator, threshold, heading);
        if (nearest is null || nearest.Id == holdShortNodeId)
        {
            return null;
        }

        double alongFt = AlongTrackFt(node.Position, threshold, heading);
        double nearestFt = AlongTrackFt(nearest.Position, threshold, heading);
        bool oppositeSide = IsLeftOfCourse(node, threshold, heading) != IsLeftOfCourse(nearest, threshold, heading);
        string? taxiway = ResolveTaxiwayName(node, currentTaxiway);
        bool sameTaxiway =
            taxiway is not null
            && ResolveTaxiwayName(nearest, currentTaxiway: null) is { } nearestTaxiway
            && taxiway.Equals(nearestTaxiway, StringComparison.OrdinalIgnoreCase);

        if (oppositeSide && ((alongFt - nearestFt <= OppositeSideBandFt) || sameTaxiway))
        {
            return null;
        }

        Log.LogTrace(
            "[EntryPoint] {Airport} {Runway} node {NodeId}: {AlongFt:F0}ft on {Taxiway} ({Side}) vs full length at {NearestFt:F0}ft → intersection",
            layout.AirportId,
            runwayDesignator,
            holdShortNodeId,
            alongFt,
            taxiway ?? "(unnamed)",
            oppositeSide ? "opposite side" : "same side",
            nearestFt
        );
        return taxiway;
    }

    /// <summary>
    /// The hold short nearest the departing threshold — the runway's full-length entrance. Null when no hold
    /// short serves this runway, in which case nothing can be classified and every node falls back to the
    /// bare runway.
    /// </summary>
    private static GroundNode? FindFullLengthHoldShort(AirportGroundLayout layout, string runwayDesignator, LatLon threshold, TrueHeading heading)
    {
        GroundNode? nearest = null;
        double nearestFt = double.MaxValue;
        foreach (var candidate in layout.GetRunwayHoldShortNodes(runwayDesignator))
        {
            double candidateFt = AlongTrackFt(candidate.Position, threshold, heading);
            if (candidateFt < nearestFt)
            {
                nearestFt = candidateFt;
                nearest = candidate;
            }
        }

        return nearest;
    }

    /// <summary>Which side of the runway a hold short sits on, relative to the takeoff direction.</summary>
    private static bool IsLeftOfCourse(GroundNode node, LatLon threshold, TrueHeading heading) =>
        GeoMath.SignedCrossTrackDistanceNm(node.Position, threshold, heading) < 0;

    /// <summary>
    /// The departing end's threshold plus the heading down the pavement. <see cref="GroundRunway.Coordinates"/>
    /// run from the first-named end to the second, so the end the caller named picks which coordinate is the
    /// start — matching <c>GroundCommandHandler.TryResolveAirTaxiDestination</c>.
    /// </summary>
    private static bool TryBuildAxis(AirportGroundLayout layout, string runwayDesignator, out LatLon threshold, out TrueHeading heading)
    {
        threshold = default;
        heading = default;

        var runway = layout.FindRunway(runwayDesignator);
        if (runway is null || runway.Coordinates.Count < 2)
        {
            return false;
        }

        bool isFirstEnd = runway.Id.End1.Equals(RunwayIdentifier.NormalizeDesignator(runwayDesignator), StringComparison.OrdinalIgnoreCase);
        var start = isFirstEnd ? runway.Coordinates[0] : runway.Coordinates[^1];
        var end = isFirstEnd ? runway.Coordinates[^1] : runway.Coordinates[0];

        threshold = new LatLon(start.Lat, start.Lon);
        heading = new TrueHeading(GeoMath.BearingTo(threshold, new LatLon(end.Lat, end.Lon)));
        return true;
    }

    /// <summary>
    /// Distance down the runway from the departing threshold, in feet. Negative for a hold short seated
    /// slightly behind the pavement end, which is still full length.
    /// </summary>
    private static double AlongTrackFt(LatLon position, LatLon threshold, TrueHeading heading) =>
        GeoMath.AlongTrackDistanceNm(position, threshold, heading) * GeoMath.FeetPerNm;

    /// <summary>
    /// The taxiway a hold short sits on, taken from its straight edges. Arcs are skipped because a fillet at
    /// a hold short carries a joined name ("J - P") that is not what a controller would say. When the node
    /// carries more than one name, <paramref name="currentTaxiway"/> breaks the tie if it is one of them;
    /// otherwise the most-connected name wins, ordinal-first for a stable answer.
    /// </summary>
    private static string? ResolveTaxiwayName(GroundNode node, string? currentTaxiway)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in node.Edges)
        {
            if (edge is not GroundEdge straight || straight.IsRunwayCenterline || straight.IsRamp || string.IsNullOrEmpty(straight.TaxiwayName))
            {
                continue;
            }
            counts[straight.TaxiwayName] = counts.GetValueOrDefault(straight.TaxiwayName) + 1;
        }

        if (counts.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(currentTaxiway) && counts.ContainsKey(currentTaxiway))
        {
            return currentTaxiway.ToUpperInvariant();
        }

        string best = counts.Keys.First();
        foreach (var (name, count) in counts)
        {
            if ((count > counts[best]) || ((count == counts[best]) && (string.CompareOrdinal(name, best) < 0)))
            {
                best = name;
            }
        }

        return best.ToUpperInvariant();
    }
}
