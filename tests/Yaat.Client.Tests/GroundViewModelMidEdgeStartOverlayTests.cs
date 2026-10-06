using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Testing;

namespace Yaat.Client.Tests;

/// <summary>
/// Issue #880: an aircraft resting mid-way along a long straight taxiway starts its taxi on that taxiway, at the edge's
/// endpoint ahead, or turns about to the endpoint behind when the route from the one ahead would come back over the edge.
/// The overlay reconstructs the route client-side from the taxiway the aircraft is on, else it draws the route from the
/// nearest node, which at KOAK mid-C west of H is on the parallel D; it draws the turn about only while the simulation
/// reports one pending (<see cref="AircraftModel.TaxiTurnAboutPending"/>), and never decides one itself.
/// </summary>
public class GroundViewModelMidEdgeStartOverlayTests
{
    private const double MinLongEdgeFt = 1000.0;

    private static GroundViewModel MakeViewModel()
    {
        var connection = new ServerConnection();
        return new GroundViewModel(connection, sendCommand: (_, _, _) => Task.CompletedTask);
    }

    private static AirportGroundLayout? LoadOakLayout()
    {
        string path = Path.Combine("TestData", "oak.geojson");
        return File.Exists(path) ? GeoJsonParser.Parse("OAK", File.ReadAllText(path), null, FilletMode.Standard) : null;
    }

    [Fact]
    public void MidC_FacingH_OverlayStartsOnC()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "B738", "C B", "28R", turnAboutPending: false));

        Assert.NotNull(drawn);
        TaxiRouteSegment firstOnGraph = drawn!.Segments.First(s => !VirtualNode.IsVirtualEdge(s.Edge.Edge));
        Assert.True(
            firstOnGraph.Edge.Edge.MatchesTaxiway("C"),
            $"the overlay joins the graph on {firstOnGraph.TaxiwayName} ({firstOnGraph.FromNodeId}->{firstOnGraph.ToNodeId}), not C"
        );
        Assert.DoesNotContain(drawn.Segments, s => s.Edge.Edge.MatchesTaxiway("D"));
    }

    /// <summary>
    /// A C172 mid-C facing H, cleared via C J: J branches off C behind it, so the route from the C node ahead would come
    /// straight back over the edge it stands on. The simulation turns it about to the edge's far node and reports the turn
    /// about pending, and the overlay draws the same: the free-space leg back to that node, and no segment on the edge.
    /// </summary>
    [Fact]
    public void MidC_DestinationBehind_OverlayTurnsAboutToTheFarNode()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "C172", "C J", "", turnAboutPending: true));

        Assert.NotNull(drawn);
        TaxiRouteSegment first = drawn!.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.DoesNotContain(drawn.Segments, s => ReferenceEquals(s.Edge.Edge, occupied));
    }

    /// <summary>
    /// The same C172 pose and clearance with no turn about pending (the simulation did not turn it about, or it has
    /// finished the leg): the overlay never decides a turn about itself, so it draws no leg back to the far node.
    /// </summary>
    [Fact]
    public void MidC_DestinationBehind_NoTurnAboutPending_OverlayDrawsNoTurnAbout()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "C172", "C J", "", turnAboutPending: false));

        Assert.DoesNotContain(drawn?.Segments ?? [], s => VirtualNode.IsVirtualEdge(s.Edge.Edge) && (s.ToNodeId == farEnd.Id));
    }

    /// <summary>
    /// A B738 at the same spot angled 45° across C: a jet not lined up with its taxiway turns about like any other
    /// aircraft in the sim, which reports the turn about pending, so the overlay draws the same turn about to the far node.
    /// </summary>
    [Fact]
    public void MidC_DestinationBehind_AngledJetOverlayTurnsAboutToTheFarNode()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);
        AircraftModel jet = MidCFacingH(tangentCut, farEnd, "B738", "C J", "", turnAboutPending: true);
        jet.Heading = new TrueHeading(jet.Heading.Degrees + AcrossEdgeDeg);

        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));

        TaxiRoute? drawn = vm.ResolveRemainingRoute(jet);

        Assert.NotNull(drawn);
        TaxiRouteSegment first = drawn!.Segments[0];
        Assert.True(VirtualNode.IsVirtualEdge(first.Edge.Edge), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(farEnd.Id, first.ToNodeId);
        Assert.DoesNotContain(drawn.Segments, s => ReferenceEquals(s.Edge.Edge, occupied));
    }

    /// <summary>
    /// A B738 lined up along C at the same spot with a turn about pending: the simulation turns a lined-up jet about only on
    /// a clearance no controller issued, and then keeps the route from the C node ahead, whose first segment reverses over
    /// the C edge to the far node. The overlay draws that route, not a leg back from the far node.
    /// </summary>
    [Fact]
    public void MidC_DestinationBehind_LinedUpJetOverlayKeepsTheRouteAheadReversingOverC()
    {
        if (LoadOakLayout() is not { } layout)
        {
            return; // test data absent — skip
        }

        TestVnasData.EnsureInitialized();
        (GroundNode tangentCut, GroundNode farEnd) = LongCEdgeWestOfH(layout);
        IGroundEdge occupied = tangentCut.Edges.First(e => (e is GroundEdge) && (e.OtherNode(tangentCut) == farEnd));
        GroundViewModel vm = MakeViewModel();
        vm.SetDomainLayoutForTesting(layout);

        TaxiRoute? drawn = vm.ResolveRemainingRoute(MidCFacingH(tangentCut, farEnd, "B738", "C J", "", turnAboutPending: true));

        Assert.NotNull(drawn);
        TaxiRouteSegment first = drawn!.Segments[0];
        Assert.True(ReferenceEquals(first.Edge.Edge, occupied), $"segment 0 is the {first.TaxiwayName} edge {first.FromNodeId}->{first.ToNodeId}");
        Assert.Equal(tangentCut.Id, first.FromNodeId);
        Assert.Equal(farEnd.Id, first.ToNodeId);
    }

    /// <summary>How far the angled jet's heading is turned off C, past the alignment within which a jet refuses to turn about.</summary>
    private const double AcrossEdgeDeg = 45.0;

    /// <summary>
    /// A stopped <paramref name="type"/> 35% of the way along C from the tangent cut to the far end, facing the tangent cut,
    /// with the simulation's turn-about flag as given.
    /// </summary>
    private static AircraftModel MidCFacingH(
        GroundNode tangentCut,
        GroundNode farEnd,
        string type,
        string taxiRoute,
        string runway,
        bool turnAboutPending
    ) =>
        new()
        {
            Callsign = "DAL880",
            AircraftType = type,
            Position = new LatLon(
                tangentCut.Position.Lat + ((farEnd.Position.Lat - tangentCut.Position.Lat) * 0.35),
                tangentCut.Position.Lon + ((farEnd.Position.Lon - tangentCut.Position.Lon) * 0.35)
            ),
            Heading = new TrueHeading(GeoMath.BearingTo(farEnd.Position, tangentCut.Position)),
            CurrentTaxiway = "C",
            TaxiRoute = taxiRoute,
            AssignedRunway = runway,
            HasActiveTaxiRoute = true,
            TaxiTurnAboutPending = turnAboutPending,
            IsOnGround = true,
        };

    /// <summary>The long straight C edge west of the C/H junction, away from C/B: its node nearer H, then its far node.</summary>
    private static (GroundNode TangentCut, GroundNode FarEnd) LongCEdgeWestOfH(AirportGroundLayout layout)
    {
        GroundNode? junctionCH = layout.FindIntersectionNode("C", "H");
        GroundNode? junctionCB = layout.FindIntersectionNode("C", "B");
        Assert.NotNull(junctionCH);
        Assert.NotNull(junctionCB);

        double awayFromB = (GeoMath.BearingTo(junctionCH.Position, junctionCB.Position) + 180.0) % 360.0;
        GroundNode tangentCut = NextAlongC(junctionCH, awayFromB);
        GroundNode farEnd = NextAlongC(tangentCut, GeoMath.BearingTo(junctionCH.Position, tangentCut.Position));
        double edgeFt = GeoMath.DistanceNm(tangentCut.Position, farEnd.Position) * GeoMath.FeetPerNm;
        Assert.True(edgeFt > MinLongEdgeFt, $"C edge {tangentCut.Id}-{farEnd.Id} is {edgeFt:F0} ft, expected a long edge");
        return (tangentCut, farEnd);
    }

    private static GroundNode NextAlongC(GroundNode node, double bearingDeg)
    {
        GroundNode? best = node
            .Edges.Where(e => (e is GroundEdge) && e.MatchesTaxiway("C"))
            .Select(e => e.OtherNode(node))
            .MinBy(other => GeoMath.AbsBearingDifference(GeoMath.BearingTo(node.Position, other.Position), bearingDeg));
        Assert.NotNull(best);
        return best;
    }
}
