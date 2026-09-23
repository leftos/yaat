using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Issue #451: SFO's spot "30" is a GeoJSON marker joined to no taxiway (zero edges) that sits beside B at the Q
/// junction. SKW5899, holding short of Q on B, is too far behind the junction for the heading-aware start-node
/// lookup, and the absolute-nearest fallback picked the edgeless spot, so every TAXI failed "transition infeasible
/// from node 27". A start node must be one the aircraft can leave.
/// </summary>
public class TaxiStartNodeEdgelessSpotTests
{
    public TaxiStartNodeEdgelessSpotTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TaxiFromBesideAnEdgelessSpot_ResolvesARoute()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        GroundNode? orphan = layout.FindSpotNodeByName("30");
        Assert.NotNull(orphan);
        Assert.Empty(orphan.Edges);

        // SKW5899's pose at t=870 of the S1-SFO-2 GC 28/01 bundle: holding short of Q on B.
        var ac = new AircraftState
        {
            Callsign = "SKW5899",
            AircraftType = "E75L",
            Position = new LatLon(37.62323202584031, -122.38879590369207),
            TrueHeading = new TrueHeading(297.9),
            TrueTrack = new TrueHeading(297.9),
            Altitude = 13,
            IndicatedAirspeed = 0,
            IsOnGround = true,
        };
        ac.Ground.Layout = layout;
        ac.Phases = new PhaseList();
        ac.Phases.Add(new HoldingInPositionPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        ParseResult<ParsedCommand> parsed = CommandParser.Parse("TAXI Q A");
        Assert.True(parsed.IsSuccess, parsed.Reason);
        var compound = new CompoundCommand([new ParsedBlock(null, [parsed.Value!])]);
        DispatchContext ctx = TestDispatch.Context(new Random(42), validateDctFixes: false, groundLayout: layout);

        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, ctx);

        Assert.True(result.Success, $"Expected a route, got: {result.Message}");
    }

    [Fact]
    public void FindNearestNode_SkipsNodesWithNoEdges()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        GroundNode orphan = layout.FindSpotNodeByName("30")!;

        GroundNode? nearest = layout.FindNearestNode(orphan.Position);

        Assert.NotNull(nearest);
        Assert.NotEmpty(nearest.Edges);
    }
}
