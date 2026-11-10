using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using TurnAboutBrakeLeg = Yaat.Sim.Phases.Ground.GroundNavigator.TurnAboutBrakeLeg;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The ramp-lane brake leg (<c>GroundNavigator.TryRampLaneBrakeLeg</c>) is solved only at a node the aircraft has not rolled
/// past and only at its pivot speed. A ramp-lane turn about at the node while the aircraft is past it, or still above its
/// pivot speed, would slide it back to the node; refused there, the turn about falls through to the comfortable-radius arc instead.
/// The lane, node and arrival bearing are the real ones — SFO T7A at node 542, the reversal beyond spot 7A.
/// </summary>
public class RampLaneTurnAboutBrakeLegTests(ITestOutputHelper output)
{
    private const string AircraftType = "CRJ2";

    /// <summary>The ramp taxilane the reversal is on: SFO's T7A, beyond spot 7A toward the ramp.</summary>
    private const string Lane = "T7A";

    /// <summary>The spot whose lane carries the reversal.</summary>
    private const string SpotName = "7A";

    /// <summary>The SFO layout node the turn about is solved at.</summary>
    private const int ReversalNodeId = 542;

    /// <summary>A taxiing speed well above the type's turn-about pivot speed, kts.</summary>
    private const double AbovePivotKts = 10.0;

    /// <summary>How far short of the node the at-the-node cases stand, feet.</summary>
    private const double AtTheNodeFt = 0.5;

    /// <summary>How far short of the node the braking case stands, feet.</summary>
    private const double ShortOfTheNodeFt = 60.0;

    /// <summary>How far past the node the overrun case stands, feet — inside the occupied-edge band the site gate allows.</summary>
    private const double RolledPastFt = -5.0;

    [Fact]
    public void RolledPastTheNode_IsRefused()
    {
        if (TrySolve(RolledPastFt, AbovePivotKts) is not { } outcome)
        {
            return;
        }

        Assert.False(
            outcome.Solved,
            $"{RolledPastFt:F1} ft from {Lane} node {ReversalNodeId} is past it: a ramp-lane turn about there would slide the aircraft back"
        );
    }

    /// <summary>The overrun check on its own: the same 5 ft past the node, at a speed the pivot gate would accept.</summary>
    [Fact]
    public void RolledPastTheNodeAtPivotSpeed_IsRefused()
    {
        if (TrySolve(RolledPastFt, null) is not { } outcome)
        {
            return;
        }

        Assert.False(
            outcome.Solved,
            $"{RolledPastFt:F1} ft from {Lane} node {ReversalNodeId} is past it even at the {outcome.PivotKts:F2} kt pivot: "
                + "the turn about must not slide the aircraft back to the node"
        );
    }

    [Fact]
    public void AtTheNodeAbovePivotSpeed_IsRefused()
    {
        if (TrySolve(AtTheNodeFt, AbovePivotKts) is not { } outcome)
        {
            return;
        }

        Assert.True(
            AbovePivotKts > outcome.PivotKts + GroundNavigator.TurnSpeedOvershootKts,
            $"the case needs a speed above the pivot speed plus its overshoot, and {AbovePivotKts:F1} kt is not above "
                + $"{outcome.PivotKts:F2} + {GroundNavigator.TurnSpeedOvershootKts:F2} kt"
        );
        Assert.False(
            outcome.Solved,
            $"{AtTheNodeFt:F1} ft from {Lane} node {ReversalNodeId} at {AbovePivotKts:F1} kt, above the {outcome.PivotKts:F2} kt pivot: "
                + "the turn about must wait for the rolling path, not begin at the node above pivot speed"
        );
    }

    [Fact]
    public void AtTheNodeAtPivotSpeed_TurnsAboutThere()
    {
        if (TrySolve(AtTheNodeFt, null) is not { } outcome)
        {
            return;
        }

        Assert.True(
            outcome.Solved,
            $"{AtTheNodeFt:F1} ft from {Lane} node {ReversalNodeId} at the {outcome.PivotKts:F2} kt pivot: the turn about must be solved there"
        );
        Assert.Null(outcome.Leg);
    }

    [Fact]
    public void ShortOfTheNode_BrakesToPivot()
    {
        if (TrySolve(ShortOfTheNodeFt, AbovePivotKts) is not { } outcome)
        {
            return;
        }

        Assert.True(
            outcome.Solved,
            $"{ShortOfTheNodeFt:F0} ft short of {Lane} node {ReversalNodeId} at {AbovePivotKts:F1} kt: the aircraft must brake to the "
                + $"{outcome.PivotKts:F2} kt pivot by the node"
        );
        Assert.NotNull(outcome.Leg);
    }

    /// <summary>
    /// Asks the brake leg for an SFO layout node with the aircraft placed <paramref name="toNodeFt"/> from it along the lane
    /// (positive short of the node, negative past it) at <paramref name="speedKts"/> — null for its own pivot speed.
    /// </summary>
    /// <returns>What the brake leg answered and the pivot speed it read, or null when the committed test data is missing.</returns>
    private (bool Solved, TurnAboutBrakeLeg? Leg, double PivotKts)? TrySolve(double toNodeFt, double? speedKts)
    {
        if (SfoGroundHarness.Build(output, AutoCrossRunwayMode.All) is not { } ground)
        {
            return null;
        }

        AirportGroundLayout layout = ground.Layout;
        if (!layout.Nodes.TryGetValue(ReversalNodeId, out GroundNode? node))
        {
            throw new InvalidOperationException($"SFO layout has no node {ReversalNodeId}");
        }

        (GroundEdge edge, GroundNode from) = LaneEdgeInto(layout, node);
        double forwardDeg = GeoMath.BearingTo(from.Position, node.Position);
        output.WriteLine($"{Lane} node {node.Id}: arriving from node {from.Id} on {forwardDeg:F1}, {edge.DistanceNm * GeoMath.FeetPerNm:F0} ft long");

        AircraftState aircraft = Place(layout, node, forwardDeg, toNodeFt, speedKts ?? 0.0);
        PhaseContext ctx = Context(layout, aircraft);
        double pivotKts = GroundNavigator.PivotKts(ctx);
        aircraft.IndicatedAirspeed = speedKts ?? pivotKts;

        bool solved = GroundNavigator.TryRampLaneBrakeLeg(ctx, node, forwardDeg, out TurnAboutBrakeLeg? leg);
        output.WriteLine($"  {toNodeFt:F1} ft to the node at {aircraft.IndicatedAirspeed:F2} kt (pivot {pivotKts:F2}): solved={solved}");
        return (solved, leg, pivotKts);
    }

    /// <summary>
    /// The <see cref="Lane"/> edge the aircraft arrives at <paramref name="node"/> on — the one from the spot's side, its other
    /// node nearer the spot than the other lane edge's.
    /// </summary>
    private static (GroundEdge Edge, GroundNode From) LaneEdgeInto(AirportGroundLayout layout, GroundNode node)
    {
        GroundNode spot = layout.FindSpotNodeByName(SpotName) ?? throw new InvalidOperationException($"SFO layout has no spot named '{SpotName}'");
        List<GroundEdge> lane = [.. node.Edges.OfType<GroundEdge>().Where(e => e.MatchesTaxiway(Lane))];
        if (lane.Count == 0)
        {
            throw new InvalidOperationException($"SFO node {node.Id} has no straight {Lane} edge");
        }

        GroundEdge edge = lane.MinBy(e => GeoMath.DistanceNm(e.OtherNode(node).Position, spot.Position))!;
        return (edge, edge.OtherNode(node));
    }

    /// <summary>
    /// The aircraft on <paramref name="forwardDeg"/>'s line through <paramref name="node"/>, <paramref name="toNodeFt"/> from
    /// it (positive short of the node, negative past it), nose on <paramref name="forwardDeg"/> at <paramref name="speedKts"/>.
    /// </summary>
    private static AircraftState Place(AirportGroundLayout layout, GroundNode node, double forwardDeg, double toNodeFt, double speedKts)
    {
        double towardDeg = (toNodeFt >= 0.0) ? (forwardDeg + 180.0) % 360.0 : forwardDeg;
        var aircraft = new AircraftState
        {
            Callsign = "SKW5564",
            AircraftType = AircraftType,
            Position = GeoMath.ProjectPoint(node.Position, new TrueHeading(towardDeg), Math.Abs(toNodeFt) / GeoMath.FeetPerNm),
            TrueHeading = new TrueHeading(forwardDeg),
            Altitude = 0,
            IndicatedAirspeed = speedKts,
            IsOnGround = true,
        };
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    /// <summary>A phase context over <paramref name="layout"/> carrying only <paramref name="aircraft"/>.</summary>
    private static PhaseContext Context(AirportGroundLayout layout, AircraftState aircraft) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategorization.Categorize(aircraft.AircraftType),
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
            GroundLayout = layout,
            ListAircraft = () => [aircraft],
            AircraftLookup = callsign => string.Equals(callsign, aircraft.Callsign, StringComparison.OrdinalIgnoreCase) ? aircraft : null,
        };
}
