using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Pathfinding;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// From SFO stand 9-9, <c>TAXI T9 A F 28L</c> reaches A through the mandatory-connector detour, whose ranking of A
/// entries by pavement alone let the raw T9/A junction vertex, reached with the turn still to make, beat the A end of
/// the A/T9 fillet, reached through the turn. The route then pivoted square at the vertex.
/// </summary>
public class SfoT9AFilletArcTests(ITestOutputHelper output)
{
    private const double StartHeadingTrue = 117.8; // the original bug report's recorded taxi pose for stand 9-9

    [Fact]
    public void TaxiT9AF_FromStand9_9_TurnsOntoAOverTheFilletArc()
    {
        TestVnasData.EnsureInitialized();
        if ((TestVnasData.NavigationDb is null) || (new TestAirportGroundData().GetLayout("SFO") is not { } layout))
        {
            return;
        }

        GroundNode stand = GroundNodeLookup.Parking(layout, "9-9");
        GroundNode vertex = GroundNodeLookup.Junction(layout, "T9", "A", stand);
        GroundNode arcExitOnA = GroundNodeLookup.FilletEnd(layout, "A", "T9", stand);
        GroundNode arcEntryOnT9 = GroundNodeLookup.FilletEnd(layout, "T9", "A", stand);

        TaxiRoute? route = TaxiPathfinder.ResolveExplicitPath(
            layout,
            stand.Id,
            ["T9", "A", "F"],
            out string? failReason,
            new ExplicitPathOptions
            {
                StartPose = null,
                OccupiedTaxiway = null,
                ImpliedLeadTaxiway = null,
                AircraftLengthFt = AircraftLength.ResolveFt("B77W"),
                DestinationRunway = "28L",
                StartHeadingTrue = StartHeadingTrue,
            },
            new TaxiClass(AircraftCategory.Jet, WakeTurbulenceData.WakeClass.Heavy)
        );

        Assert.Null(failReason);
        Assert.NotNull(route);
        output.WriteLine("route: " + string.Join(" ", route.Segments.Select(s => $"{s.TaxiwayName}:{s.FromNodeId}->{s.ToNodeId}")));

        TaxiRouteSegment corner = Assert.Single(route.Segments, s => (s.FromNodeId == arcEntryOnT9.Id) && (s.ToNodeId == arcExitOnA.Id));
        GroundArc arc = Assert.IsType<GroundArc>(corner.Edge.Edge);
        Assert.True(GroundNodeLookup.JoinsFillet(arc, "A", "T9"), $"corner is not the A/T9 fillet: [{string.Join(",", arc.TaxiwayNames)}]");
        Assert.DoesNotContain(route.Segments, s => (s.FromNodeId == vertex.Id) || (s.ToNodeId == vertex.Id));
    }
}
