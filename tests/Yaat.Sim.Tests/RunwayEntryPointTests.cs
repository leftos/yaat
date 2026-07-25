using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Coverage for <see cref="RunwayEntryPoint.Resolve"/> on the real OAK layout. KOAK 28R departs to the
/// west, so its full-length entrances are the two taxiway-B hold shorts at the east end (~40 ft from the
/// threshold); everything further down — E at ~1625 ft, G, H, P, J — is an intersection departure. The
/// reciprocal 10L flips that: C1 is full length and B becomes the far-end intersection.
/// Nodes are selected by their edge taxiway names, never by node id (fillet node ids are geometry-coupled).
/// </summary>
public class RunwayEntryPointTests
{
    private readonly AirportGroundLayout? _layout;

    public RunwayEntryPointTests()
    {
        TestVnasData.EnsureInitialized();
        _layout = new TestAirportGroundData().GetLayout("OAK");
    }

    /// <summary>Taxiway names on a node's straight edges — how a hold short is identified to a controller.</summary>
    private static HashSet<string> StraightTaxiways(GroundNode node) =>
        node.Edges.OfType<GroundEdge>().Select(e => e.TaxiwayName).Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private List<GroundNode> HoldShortsOn(string runway, string taxiway) =>
        _layout is null ? [] : _layout.GetRunwayHoldShortNodes(runway).Where(n => StraightTaxiways(n).SetEquals([taxiway])).ToList();

    [Fact]
    public void OppositeSidesOfTheSameEnd_AreBothFullLength_EvenOnDifferentTaxiways()
    {
        // KOAK 15 is entered from F on one side at ~27 ft and D on the other at ~123 ft. Different names, but
        // opposite sides of the same runway end — one entrance reachable from either side, so both are full
        // length.
        var foxtrot = HoldShortsOn("15", "F");
        var delta = HoldShortsOn("15", "D");
        if (_layout is null || foxtrot.Count == 0 || delta.Count == 0)
        {
            return;
        }

        Assert.All(foxtrot, node => Assert.Null(RunwayEntryPoint.Resolve(_layout, node.Id, "15", currentTaxiway: null)));
        Assert.All(delta, node => Assert.Null(RunwayEntryPoint.Resolve(_layout, node.Id, "15", currentTaxiway: null)));
    }

    [Fact]
    public void SameTaxiwayCrossingTheEndDiagonally_IsFullLengthOnBothSides()
    {
        // KOAK 33 is entered by C, which crosses the end at an angle — its two bars are ~271 ft apart
        // along-track, past the opposite-side band, but one taxiway is one entrance.
        var charlie = HoldShortsOn("33", "C");
        if (_layout is null || charlie.Count < 2)
        {
            return;
        }

        Assert.All(charlie, node => Assert.Null(RunwayEntryPoint.Resolve(_layout, node.Id, "33", currentTaxiway: null)));
    }

    [Fact]
    public void OppositeSideFarDownTheRunway_IsStillAnIntersection()
    {
        // KOAK 10L: C1 at ~44 ft is full length; J sits on the other side but ~419 ft down, well past the
        // band and on a different taxiway, so it is a real intersection departure.
        var c1 = HoldShortsOn("10L", "C1");
        var j = HoldShortsOn("10L", "J");
        if (_layout is null || c1.Count == 0 || j.Count == 0)
        {
            return;
        }

        Assert.All(c1, node => Assert.Null(RunwayEntryPoint.Resolve(_layout, node.Id, "10L", currentTaxiway: null)));
        Assert.Contains(j, node => RunwayEntryPoint.Resolve(_layout, node.Id, "10L", currentTaxiway: null) == "J");
    }

    [Fact]
    public void BothTaxiwayBHoldShorts_AreFullLengthFor28R()
    {
        var nodes = HoldShortsOn("28R", "B");
        if (_layout is null || nodes.Count == 0)
        {
            return;
        }

        Assert.Equal(2, nodes.Count);
        foreach (var node in nodes)
        {
            Assert.Null(RunwayEntryPoint.Resolve(_layout, node.Id, "28R", currentTaxiway: null));
        }
    }

    [Theory]
    [InlineData("E")]
    [InlineData("G")]
    [InlineData("H")]
    [InlineData("P")]
    public void HoldShortsDownTheRunway_AreIntersectionsFor28R(string taxiway)
    {
        var nodes = HoldShortsOn("28R", taxiway);
        if (_layout is null || nodes.Count == 0)
        {
            return;
        }

        foreach (var node in nodes)
        {
            Assert.Equal(taxiway, RunwayEntryPoint.Resolve(_layout, node.Id, "28R", currentTaxiway: null));
        }
    }

    [Fact]
    public void ReciprocalEnd_FlipsWhichHoldShortIsFullLength()
    {
        var c1 = HoldShortsOn("10L", "C1");
        var b = HoldShortsOn("10L", "B");
        if (_layout is null || c1.Count == 0 || b.Count == 0)
        {
            return;
        }

        Assert.All(c1, node => Assert.Null(RunwayEntryPoint.Resolve(_layout, node.Id, "10L", currentTaxiway: null)));
        Assert.All(b, node => Assert.Equal("B", RunwayEntryPoint.Resolve(_layout, node.Id, "10L", currentTaxiway: null)));
    }

    [Fact]
    public void TwoEntrancesOnTheSameSide_OnlyTheNearerIsFullLength()
    {
        // KSMF 17R has A3 and A both on the same side of the end, ~27 ft apart — well inside the
        // opposite-side band, so only the side test keeps them apart. Same side is always two entrances.
        var smf = new TestAirportGroundData().GetLayout("SMF");
        if (smf is null)
        {
            return;
        }

        var a3 = smf.GetRunwayHoldShortNodes("17R").Where(n => StraightTaxiways(n).SetEquals(["A3"])).ToList();
        // Taxiway A also has a bar at the far (35L) end; take the one beside A3, not that one.
        var a = smf.GetRunwayHoldShortNodes("17R")
            .Where(n => StraightTaxiways(n).SetEquals(["A"]))
            .OrderBy(n => a3.Count == 0 ? 0 : GeoMath.DistanceNm(n.Position, a3[0].Position))
            .ToList();
        if (a3.Count == 0 || a.Count == 0)
        {
            return;
        }

        Assert.Null(RunwayEntryPoint.Resolve(smf, a3[0].Id, "17R", currentTaxiway: null));
        Assert.Equal("A", RunwayEntryPoint.Resolve(smf, a[0].Id, "17R", currentTaxiway: null));
    }

    [Fact]
    public void CurrentTaxiwayHint_DoesNotOverrideTheNodesOwnTaxiway()
    {
        var nodes = HoldShortsOn("28R", "E");
        if (_layout is null || nodes.Count == 0)
        {
            return;
        }

        Assert.Equal("E", RunwayEntryPoint.Resolve(_layout, nodes[0].Id, "28R", currentTaxiway: "G"));
    }

    [Fact]
    public void UnknownRunwayOrNode_ReturnsNull()
    {
        var nodes = HoldShortsOn("28R", "E");
        if (_layout is null || nodes.Count == 0)
        {
            return;
        }

        Assert.Null(RunwayEntryPoint.Resolve(_layout, nodes[0].Id, "99X", currentTaxiway: null));
        Assert.Null(RunwayEntryPoint.Resolve(_layout, -1, "28R", currentTaxiway: null));
    }
}
