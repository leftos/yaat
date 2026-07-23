using Xunit;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Unit coverage for <see cref="RunwayDepartureQueue.UpdatePositions"/>: the per-hold-short-line
/// departure-queue ranking. Uses a real OAK layout and real 28R hold-short nodes; aircraft are placed
/// at controlled distances (via <see cref="GeoMath.ProjectPoint"/>) and given genuine phase/route
/// objects so the ranking is exercised on real geometry without a full taxi rollout.
/// </summary>
public class RunwayDepartureQueueTests
{
    private const string Runway = "28R";

    private readonly AirportGroundLayout? _layout;

    public RunwayDepartureQueueTests()
    {
        TestVnasData.EnsureInitialized();
        _layout = new TestAirportGroundData().GetLayout("OAK");
    }

    private List<GroundNode> HoldShortNodes() => _layout is null ? [] : _layout.GetRunwayHoldShortNodes(Runway);

    private AircraftState HoldingShort(string callsign, GroundNode node)
    {
        var ac = MakeGroundAircraft(callsign, node.Position);
        ac.Phases!.Add(
            new HoldingShortPhase(
                new HoldShortPoint
                {
                    NodeId = node.Id,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = Runway,
                }
            )
        );
        return ac;
    }

    private AircraftState TaxiingToward(string callsign, GroundNode node, double distanceNm)
    {
        var ac = MakeGroundAircraft(callsign, GeoMath.ProjectPoint(node.Position, new TrueHeading(90), distanceNm));
        ac.Phases!.Add(new TaxiingPhase());
        ac.Ground.AssignedTaxiRoute = new TaxiRoute
        {
            Segments = [],
            HoldShortPoints =
            [
                new HoldShortPoint
                {
                    NodeId = node.Id,
                    Reason = HoldShortReason.DestinationRunway,
                    TargetName = Runway,
                },
            ],
        };
        return ac;
    }

    private AircraftState LinedUp(string callsign, GroundNode node)
    {
        var ac = MakeGroundAircraft(callsign, node.Position);
        ac.Phases!.Add(new LinedUpAndWaitingPhase());
        return ac;
    }

    private AircraftState MakeGroundAircraft(string callsign, LatLon position)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(280),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK" },
        };
        ac.Phases = new PhaseList();
        ac.Ground.Layout = _layout;
        return ac;
    }

    [Fact]
    public void HoldingShortLead_AndTaxiingTrailer_AreNumberedOneAndTwo()
    {
        var nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        var lead = HoldingShort("LEAD", nodes[0]);
        var trailer = TaxiingToward("TRAIL", nodes[0], 0.05);

        RunwayDepartureQueue.UpdatePositions([lead, trailer]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(2, trailer.Ground.RunwayQueuePosition);
        Assert.Equal(Runway, lead.Ground.RunwayQueueRunway);
        Assert.Equal(Runway, trailer.Ground.RunwayQueueRunway);
    }

    [Fact]
    public void TaxiingBeyondProximityGate_GetsNoNumber()
    {
        var nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        var lead = HoldingShort("LEAD", nodes[0]);
        var near = TaxiingToward("NEAR", nodes[0], 0.05);
        var far = TaxiingToward("FAR", nodes[0], 0.3);

        RunwayDepartureQueue.UpdatePositions([lead, near, far]);

        Assert.Equal(1, lead.Ground.RunwayQueuePosition);
        Assert.Equal(2, near.Ground.RunwayQueuePosition);
        Assert.Equal(0, far.Ground.RunwayQueuePosition);
        Assert.Equal("", far.Ground.RunwayQueueRunway);
    }

    [Fact]
    public void LoneAircraftInLine_GetsNumberOneWithRunway()
    {
        var nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        var solo = HoldingShort("SOLO", nodes[0]);

        RunwayDepartureQueue.UpdatePositions([solo]);

        Assert.Equal(1, solo.Ground.RunwayQueuePosition);
        Assert.Equal(Runway, solo.Ground.RunwayQueueRunway);
    }

    [Fact]
    public void TwoHoldShortNodes_AreRankedIndependently()
    {
        var nodes = HoldShortNodes();
        if (nodes.Count < 2)
        {
            return;
        }

        var leadA = HoldingShort("LEADA", nodes[0]);
        var trailA = TaxiingToward("TRAILA", nodes[0], 0.05);
        var leadB = HoldingShort("LEADB", nodes[1]);
        var trailB = TaxiingToward("TRAILB", nodes[1], 0.05);

        RunwayDepartureQueue.UpdatePositions([leadA, trailA, leadB, trailB]);

        Assert.Equal(1, leadA.Ground.RunwayQueuePosition);
        Assert.Equal(2, trailA.Ground.RunwayQueuePosition);
        Assert.Equal(1, leadB.Ground.RunwayQueuePosition);
        Assert.Equal(2, trailB.Ground.RunwayQueuePosition);
    }

    [Fact]
    public void LinedUpAircraft_LeavesTheLine_TaxiingTrailersRankFromOne()
    {
        var nodes = HoldShortNodes();
        if (nodes.Count == 0)
        {
            return;
        }

        var linedUp = LinedUp("LUAW", nodes[0]);
        var near = TaxiingToward("NEAR", nodes[0], 0.05);
        var far = TaxiingToward("FAR", nodes[0], 0.08);

        RunwayDepartureQueue.UpdatePositions([linedUp, near, far]);

        Assert.Equal(0, linedUp.Ground.RunwayQueuePosition);
        Assert.Equal(1, near.Ground.RunwayQueuePosition);
        Assert.Equal(2, far.Ground.RunwayQueuePosition);
    }
}
