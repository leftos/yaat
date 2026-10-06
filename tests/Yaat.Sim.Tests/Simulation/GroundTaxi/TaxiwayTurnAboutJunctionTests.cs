using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A turn about centred on a taxiway's centreline is for an aircraft standing inside the taxiway edge it reverses over. A
/// reversal met mid-route at a junction — a hairpin onto an acute connector, a node reversal the fillets could not round —
/// is arrived at the corner speed with the aircraft at the node, and keeps rounding the corner at its corner speed rather
/// than taking the jog and the tight radius's walking-pace pivot.
/// </summary>
public class TaxiwayTurnAboutJunctionTests(ITestOutputHelper output)
{
    /// <summary>A route bend at least this sharp (deg) is a reversal (the navigator's reversal entry threshold).</summary>
    private const double ReversalDeg = 135.0;

    /// <summary>The outgoing leg is at least this long, so a reversal aimed off the junction has a node to aim at.</summary>
    private const double MinOutgoingFt = 50.0;

    /// <summary>The speed a piston arrives at a sharp junction with: the slow-turn creep.</summary>
    private const double ArrivalSpeedKts = CategoryPerformance.SlowTurnSpeedKts;

    /// <summary>
    /// How far short of the junction node the tangent-point pose stands (ft): inside the piston's 8 ft tight-turn radius, so
    /// a turn about centred on the edge would not fit before the node.
    /// </summary>
    private const double TangentPointBackFt = 4.0;

    private static readonly string[] Airports = ["OAK", "SFO"];

    /// <summary>
    /// A C172 arriving at a real junction where two straight movement-area taxiway edges meet at a reversal of 135° or more,
    /// set up on the outgoing edge with its heading still along the incoming one: the entry turn is played at the corner
    /// speed, not the turn about's pivot speed.
    /// </summary>
    [Fact]
    public void MidRouteReversalAtAJunction_KeepsTheCornerRounding()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        (AirportGroundLayout Layout, GroundEdge Incoming, GroundNode Junction, GroundEdge Outgoing)? hairpin = Airports
            .Select(groundData.GetLayout)
            .OfType<AirportGroundLayout>()
            .Select(FirstHairpin)
            .FirstOrDefault(found => found is not null);
        Assert.True(hairpin is not null, $"no junction at {string.Join("/", Airports)} joins two straight taxiway edges at a reversal");

        (AirportGroundLayout layout, GroundEdge incoming, GroundNode junction, GroundEdge outgoing) = hairpin.Value;
        GroundNode from = incoming.OtherNode(junction);
        GroundNode to = outgoing.OtherNode(junction);
        double arrivalDeg = GeoMath.BearingTo(from.Position, junction.Position);
        output.WriteLine(
            $"{layout.AirportId}: {incoming.TaxiwayName} {from.Id}->{junction.Id} then {outgoing.TaxiwayName} {junction.Id}->{to.Id}, "
                + $"{GeoMath.AbsBearingDifference(arrivalDeg, GeoMath.BearingTo(junction.Position, to.Position)):F0} deg"
        );

        var route = new TaxiRoute
        {
            Segments = [Segment(incoming, from, junction), Segment(outgoing, junction, to)],
            HoldShortPoints = [],
            CurrentSegmentIndex = 1,
        };
        var aircraft = new AircraftState
        {
            Callsign = "N424JT",
            AircraftType = "C172",
            Position = junction.Position,
            TrueHeading = new TrueHeading(arrivalDeg),
            TrueTrack = new TrueHeading(arrivalDeg),
            IndicatedAirspeed = ArrivalSpeedKts,
            IsOnGround = true,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 0.25,
            Runway = null,
            FieldElevation = 0,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

        var nav = new GroundNavigator { MaxSpeedKts = 15.0 };
        nav.SetupSegment(route, ctx, _ => true);
        nav.Tick(ctx, isLastSegment: true, _ => true);
        output.WriteLine($"entry turn target speed {ctx.Targets.TargetSpeed:F2} kt");

        Assert.True(
            ctx.Targets.TargetSpeed >= CategoryPerformance.SlowTurnSpeedKts,
            $"the reversal at junction {junction.Id} was played at {ctx.Targets.TargetSpeed:F2} kt, below the "
                + $"{CategoryPerformance.SlowTurnSpeedKts:F0} kt corner rounding: it took the turn about meant for an aircraft inside the edge"
        );
    }

    /// <summary>
    /// The same reversal reached a few feet short of the junction node instead of at it: the aircraft stands on the
    /// movement-area edge it reverses back over, the tangent point of its turn just off the node. It is inside the edge it
    /// reverses over, but within the tight-turn radius of that node, so a turn about centred on the edge would not fit and
    /// it keeps the corner rounding — the same outcome as the at-node test. This is the tangent-point pose
    /// <c>TurnAboutTaxiwayEdge</c>'s distance guard exists for: with the guard removed the pose pivots at the walking-pace
    /// turn-about speed instead (verified by hand).
    /// </summary>
    [Fact]
    public void MidRouteReversalAtATangentPointShortOfTheNode_KeepsTheCornerRounding()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData();
        (AirportGroundLayout Layout, GroundEdge Incoming, GroundNode Junction, GroundEdge Outgoing)? hairpin = Airports
            .Select(groundData.GetLayout)
            .OfType<AirportGroundLayout>()
            .Select(FirstHairpin)
            .FirstOrDefault(found => found is not null);
        Assert.True(hairpin is not null, $"no junction at {string.Join("/", Airports)} joins two straight taxiway edges at a reversal");

        (AirportGroundLayout layout, GroundEdge incoming, GroundNode junction, _) = hairpin.Value;
        GroundNode from = incoming.OtherNode(junction);
        double arrivalDeg = GeoMath.BearingTo(from.Position, junction.Position);
        LatLon position = GeoMath.ProjectPoint(junction.Position, new TrueHeading(arrivalDeg + 180.0), TangentPointBackFt / GeoMath.FeetPerNm);
        Assert.True(
            ReferenceEquals(incoming, layout.FindOccupiedTaxiEdge(position)),
            $"the pose {TangentPointBackFt:F0} ft short of node {junction.Id} is not on the {incoming.TaxiwayName} edge it reverses over"
        );
        Assert.True(
            GroundNavigator.IsTurnAboutTaxiway(incoming, layout),
            $"{incoming.TaxiwayName} {from.Id}-{junction.Id} is not a turn-about taxiway"
        );

        var route = new TaxiRoute
        {
            Segments = [Segment(incoming, from, junction), Segment(incoming, junction, from)],
            HoldShortPoints = [],
            CurrentSegmentIndex = 1,
        };
        var aircraft = new AircraftState
        {
            Callsign = "N424JT",
            AircraftType = "C172",
            Position = position,
            TrueHeading = new TrueHeading(arrivalDeg),
            TrueTrack = new TrueHeading(arrivalDeg),
            IndicatedAirspeed = ArrivalSpeedKts,
            IsOnGround = true,
        };
        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Piston,
            DeltaSeconds = 0.25,
            Runway = null,
            FieldElevation = 0,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };

        var nav = new GroundNavigator { MaxSpeedKts = 15.0 };
        nav.SetupSegment(route, ctx, _ => true);
        nav.Tick(ctx, isLastSegment: true, _ => true);
        output.WriteLine(
            $"{layout.AirportId}: {TangentPointBackFt:F0} ft short of {junction.Id} on {incoming.TaxiwayName}, entry turn target speed {ctx.Targets.TargetSpeed:F2} kt"
        );

        Assert.True(
            ctx.Targets.TargetSpeed >= CategoryPerformance.SlowTurnSpeedKts,
            $"the reversal {TangentPointBackFt:F0} ft short of junction {junction.Id} was played at {ctx.Targets.TargetSpeed:F2} kt, below the "
                + $"{CategoryPerformance.SlowTurnSpeedKts:F0} kt corner rounding: it took the turn about meant for an aircraft with room to pivot"
        );
    }

    /// <summary>
    /// The first junction (by node id) with two straight turn-about taxiway edges (<see cref="GroundNavigator.IsTurnAboutTaxiway"/>)
    /// that meet at a reversal of at least <see cref="ReversalDeg"/>, the outgoing edge at least <see cref="MinOutgoingFt"/> long.
    /// </summary>
    private static (AirportGroundLayout, GroundEdge, GroundNode, GroundEdge)? FirstHairpin(AirportGroundLayout layout)
    {
        foreach (GroundNode junction in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            GroundEdge[] edges = [.. junction.Edges.OfType<GroundEdge>().Where(e => GroundNavigator.IsTurnAboutTaxiway(e, layout))];
            foreach (GroundEdge incoming in edges)
            {
                double arrivalDeg = GeoMath.BearingTo(incoming.OtherNode(junction).Position, junction.Position);
                GroundEdge? outgoing = edges.FirstOrDefault(e =>
                    (e != incoming)
                    && ((e.DistanceNm * GeoMath.FeetPerNm) >= MinOutgoingFt)
                    && (GeoMath.AbsBearingDifference(arrivalDeg, GeoMath.BearingTo(junction.Position, e.OtherNode(junction).Position)) >= ReversalDeg)
                );
                if (outgoing is not null)
                {
                    return (layout, incoming, junction, outgoing);
                }
            }
        }

        return null;
    }

    private static TaxiRouteSegment Segment(GroundEdge edge, GroundNode from, GroundNode to) =>
        new() { TaxiwayName = edge.TaxiwayName, Edge = edge.Directed(from, to) };
}
