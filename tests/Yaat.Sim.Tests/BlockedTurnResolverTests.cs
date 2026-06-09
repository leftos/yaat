using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="BlockedTurnResolver"/> against the real SFO south L/F intersection (apex node
/// #279). The blocked turn forbids the sharp L→F pivot so aircraft use the LF connector instead, and hides
/// only that corner's fillet arc. Exact GeoJSON vertices from <c>TestData/sfo.geojson</c>.
/// </summary>
public class BlockedTurnResolverTests
{
    // SFO south L/F apex polyline (Maxim's coordinates). WP1+WP2 are "the same point" on L/F (the apex).
    private static readonly OneWayPoint L = new(37.61494338638182, -122.37339328086573, "L"); // taxiway L, snaps near #325
    private static readonly OneWayPoint ApexA = new(37.6161316665853, -122.37260390313256, "L"); // apex #279
    private static readonly OneWayPoint ApexB = new(37.616129801005414, -122.3726033797592, "F"); // apex #279 (duplicate)
    private static readonly OneWayPoint F = new(37.615463060496644, -122.37101193249117, "F"); // taxiway F, snaps near #280

    private static BlockedTurn SfoTurn() => new([L, ApexA, ApexB, F], "SFO L/F apex");

    private static AirportGroundLayout? LoadSfo()
    {
        string path = Path.Combine("TestData", "sfo.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("SFO", File.ReadAllText(path), null) : null;
    }

    [Fact]
    public void BlocksThePivotTurn_Bidirectional_ThroughTheApex()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var result = BlockedTurnResolver.Resolve(layout, [SfoTurn()]);
        Assert.NotEmpty(result.ForbiddenTurns);

        int apexId = layout.FindNearestNode(ApexA.Lat, ApexA.Lon)!.Id;

        foreach (var (prev, apex, next) in result.ForbiddenTurns)
        {
            Assert.Equal(apexId, apex); // every blocked pivot is the L/F apex, not a collinear arm node
            Assert.Contains((next, apex, prev), result.ForbiddenTurns); // bidirectional
        }
    }

    [Fact]
    public void HidesExactlyTheLFCornerArc_OtherCornersRemain()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var result = BlockedTurnResolver.Resolve(layout, [SfoTurn()]);
        Assert.NotEmpty(result.HiddenArcPairs);

        // Every hidden arc bridges L and F (the blocked corner), and is a real arc in the layout.
        foreach (var (a, b) in result.HiddenArcPairs)
        {
            var arc = ArcByPair(layout, a, b);
            Assert.NotNull(arc);
            Assert.True(arc!.MatchesTaxiway("L") && arc.MatchesTaxiway("F"));
        }

        // The junction has several L/F corner arcs; only the blocked one is hidden — "the other 3 still apply".
        var lfArcs = layout.Arcs.Where(a => a.MatchesTaxiway("L") && a.MatchesTaxiway("F")).ToList();
        int hiddenLf = lfArcs.Count(a => result.IsHiddenArc(a.Nodes[0].Id, a.Nodes[1].Id));
        Assert.True(lfArcs.Count > hiddenLf, "other L/F corner arcs must remain drawn");

        // Sibling F/F1 corner arcs at the same apex are never hidden.
        foreach (var arc in layout.Arcs.Where(a => a.MatchesTaxiway("F") && a.MatchesTaxiway("F1")))
        {
            Assert.False(result.IsHiddenArc(arc.Nodes[0].Id, arc.Nodes[1].Id));
        }
    }

    [Fact]
    public void HiddenArc_IsAlsoForbiddenToRoute_BothDirections()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var result = BlockedTurnResolver.Resolve(layout, [SfoTurn()]);
        Assert.NotEmpty(result.HiddenArcPairs);

        foreach (var (a, b) in result.HiddenArcPairs)
        {
            Assert.Contains((a, b), result.ForbiddenArcMoves);
            Assert.Contains((b, a), result.ForbiddenArcMoves);
        }
    }

    [Fact]
    public void DuplicateMiddleWaypoint_CollapsesToSingleApex()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        // The 4-point path (duplicate apex) must resolve identically to a 3-point path (single apex).
        var withDuplicate = BlockedTurnResolver.Resolve(layout, [new BlockedTurn([L, ApexA, ApexB, F], null)]);
        var singleApex = BlockedTurnResolver.Resolve(layout, [new BlockedTurn([L, ApexA, F], null)]);

        Assert.True(withDuplicate.ForbiddenTurns.SetEquals(singleApex.ForbiddenTurns));
        Assert.True(withDuplicate.HiddenArcPairs.SetEquals(singleApex.HiddenArcPairs));
    }

    [Fact]
    public void EmptyTurns_ProduceEmptyResult()
    {
        var layout = LoadSfo();
        if (layout is null)
        {
            return;
        }

        var result = BlockedTurnResolver.Resolve(layout, []);
        Assert.Empty(result.ForbiddenTurns);
        Assert.Empty(result.ForbiddenArcMoves);
        Assert.Empty(result.HiddenArcPairs);
    }

    private static GroundArc? ArcByPair(AirportGroundLayout layout, int a, int b) =>
        layout.Arcs.FirstOrDefault(arc => (arc.Nodes[0].Id == a && arc.Nodes[1].Id == b) || (arc.Nodes[0].Id == b && arc.Nodes[1].Id == a));
}
