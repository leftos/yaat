namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// What a connector detour's bridge may not drive, judged move by move by
/// <see cref="SearchContext.BridgeMoveRefusal(BridgeRules, BridgeProgress, GroundNode, IGroundEdge, GroundNode)"/>, the one
/// judgement the search and the post-hoc filter both call. A bridge never runs along a runway: the one exception is
/// <see cref="StartRunway"/>, the runway the aircraft stands on, which the bridge may use only to leave it, within
/// <see cref="SearchContext.MaxStartRunwayExitFt"/>. Between two named taxiways its connector, the part from
/// <see cref="FromTaxiway"/> on, drives none of <see cref="ExcludedTaxiways"/>, the letter-only taxiways the clearance left out.
/// </summary>
/// <param name="StartRunway">The centreline name of the runway the bridge starts on, or null when it starts elsewhere.</param>
/// <param name="FromTaxiway">The taxiway the transition leaves; the connector starts where the bridge first touches it.</param>
/// <param name="HeadOnFromTaxiway">
/// Whether the bridge starts on <paramref name="FromTaxiway"/>, so its connector starts at its first move. False from a stand or
/// ramp start, where the lead onto the taxiway is the start leg and the connector starts at the bridge's first node on it.
/// </param>
/// <param name="ExcludedTaxiways">The letter-only taxiways the connector may not drive; empty where letters are not judged.</param>
public sealed record BridgeRules(string? StartRunway, string FromTaxiway, bool HeadOnFromTaxiway, IReadOnlySet<string> ExcludedTaxiways);

/// <summary>
/// What a bridge has driven before one of its moves, the inputs the move is judged on: the edge it arrives on (before the first
/// move, the last edge of the head the bridge leaves from; null only when the bridge starts the route), the feet driven along
/// <see cref="BridgeRules.StartRunway"/> since the bridge's start (null once it has driven anything else), and whether its
/// connector has begun, the bridge having touched <see cref="BridgeRules.FromTaxiway"/>. The search carries it on each partial
/// route (<see cref="PartialRoute.BridgeProgress"/>) and keys its closed set on it; the post-hoc filter carries it forward over
/// the bridge's edges.
/// </summary>
public readonly record struct BridgeProgress(IGroundEdge? Arrival, double? StartRunwayFt, bool OnConnector)
{
    /// <summary>The progress before a bridge's first move, leaving a head whose last edge is <paramref name="arrival"/>.</summary>
    public static BridgeProgress AtStart(IGroundEdge? arrival, BridgeRules rules) => new(arrival, 0.0, rules.HeadOnFromTaxiway);

    /// <summary>This progress after driving <paramref name="edge"/> out of <paramref name="from"/>.</summary>
    public BridgeProgress After(GroundNode from, IGroundEdge edge, BridgeRules rules)
    {
        double? startRunwayFt = null;
        if ((StartRunwayFt is { } runFt) && (rules.StartRunway is { } runway) && edge.IsRunwayCenterline && edge.MatchesTaxiway(runway))
        {
            startRunwayFt = runFt + (edge.DistanceNm * GeoMath.FeetPerNm);
        }

        bool onConnector = OnConnector || edge.MatchesTaxiway(rules.FromTaxiway) || from.Edges.Any(e => e.MatchesTaxiway(rules.FromTaxiway));
        return new BridgeProgress(edge, startRunwayFt, onConnector);
    }
}
