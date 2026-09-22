using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A bar the aircraft could not make carries <see cref="HoldShortPoint.Unable"/> and a stop position the taxi
/// phase moved forward to the point the aircraft can actually reach. Both have to survive a snapshot
/// round-trip — a rewind that dropped the flag would put the painted bar back in front of an aircraft already
/// past its braking distance — and the annotator's position recompute, which runs on every later hold-short
/// change and would otherwise walk the moved stop back onto the line nobody is going to make.
/// </summary>
public class UnableHoldShortSnapshotTests(ITestOutputHelper output)
{
    /// <summary>Shortest edge (ft) worth hanging a taxiway hold-short on, so the setback has somewhere to go.</summary>
    private const double MinEdgeFt = 300.0;

    /// <summary>Fuselage length (ft) the recompute is driven with; any value positions the bar.</summary>
    private const double AircraftLengthFt = 100.0;

    private const string Callsign = "N9002T";

    /// <summary>
    /// A piston, not a jet: its 2 kt/s taxi brake rate stretches the stop from a jet's two seconds to about
    /// ten, which is what leaves the aircraft still taxiing — and so still running the code under test — when
    /// the second hold-short lands two ticks after the restore.
    /// </summary>
    private const string Type = "C172";

    private const string TaxiClearance = "TAXI B K A T7 @E2";

    /// <summary>How far east on B the scripted aircraft starts, so it is at taxi speed by the T bar.</summary>
    private const double RunUpFt = 1800.0;

    /// <summary>How close to the B/T junction the scripted case issues its hold-short — inside the braking distance.</summary>
    private const double IssueWithinFt = 200.0;

    /// <summary>Ground speed (kts) the aircraft must have reached before the hold-short is issued.</summary>
    private const double IssueSpeedKts = 16.0;

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    [Fact]
    public void TaxiRoute_RoundTrip_PreservesUnableAndTheMovedStop()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (TaxiRoute Route, int NodeId)? built = BuildOneSegmentRoute(layout);
        if (built is null)
        {
            return;
        }

        (TaxiRoute route, int nodeId) = built.Value;
        GroundNode node = layout.Nodes[nodeId];
        double movedLat = node.Position.Lat + 0.0001;
        double movedLon = node.Position.Lon + 0.0001;
        route.HoldShortPoints.Add(
            new HoldShortPoint
            {
                NodeId = nodeId,
                Reason = HoldShortReason.ExplicitHoldShort,
                TargetName = "T",
                Unable = true,
                Latitude = movedLat,
                Longitude = movedLon,
            }
        );

        TaxiRouteDto dto = route.ToSnapshot();
        var restored = TaxiRoute.FromSnapshot(dto, layout);

        Assert.NotNull(restored);
        HoldShortPoint bar = Assert.Single(restored!.HoldShortPoints);
        Assert.True(bar.Unable, "the unable flag did not survive the snapshot");
        Assert.Equal(movedLat, bar.Latitude!.Value, 9);
        Assert.Equal(movedLon, bar.Longitude!.Value, 9);
    }

    /// <summary>A snapshot taken before the flag existed restores a makeable bar, which is what those routes were.</summary>
    [Fact]
    public void TaxiRoute_LegacySnapshotWithoutUnable_RestoresMakeable()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        var dto = new TaxiRouteDto
        {
            Segments = [],
            CurrentSegmentIndex = 0,
            HoldShortPoints =
            [
                new HoldShortPointDto
                {
                    NodeId = 999,
                    RunwayId = "T",
                    IsSatisfied = false,
                },
            ],
        };

        var restored = TaxiRoute.FromSnapshot(dto, layout);

        Assert.NotNull(restored);
        Assert.False(restored!.HoldShortPoints[0].Unable);
    }

    /// <summary>
    /// The recompute a later hold-short change triggers positions every ordinary bar and leaves an unable one
    /// exactly where the taxi phase put it: that point, not the painted line, is the stop being flown.
    /// </summary>
    [Fact]
    public void ComputeHoldShortPositions_LeavesAnUnableBarAtTheMovedStop()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        (TaxiRoute Route, int NodeId)? built = BuildOneSegmentRoute(layout);
        if (built is null)
        {
            return;
        }

        (TaxiRoute route, int nodeId) = built.Value;
        var bar = new HoldShortPoint
        {
            NodeId = nodeId,
            Reason = HoldShortReason.ExplicitHoldShort,
            TargetName = "T",
        };
        route.HoldShortPoints.Add(bar);

        // Control: this is a bar the annotator does position, so the second pass below would move it back.
        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, AircraftLengthFt);
        Assert.NotNull(bar.Latitude);
        Assert.NotNull(bar.Longitude);

        double movedLat = bar.Latitude!.Value + 0.0002;
        double movedLon = bar.Longitude!.Value + 0.0002;
        bar.Unable = true;
        bar.Latitude = movedLat;
        bar.Longitude = movedLon;

        HoldShortAnnotator.ComputeHoldShortPositions(layout, route, AircraftLengthFt);

        Assert.Equal(movedLat, bar.Latitude!.Value, 9);
        Assert.Equal(movedLon, bar.Longitude!.Value, 9);
    }

    /// <summary>
    /// The moved stop survives a restore as a stop that has already been moved. <c>TaxiingPhase</c> moves an
    /// unable bar once and remembers which bar it was; that memory has to ride the phase snapshot, or a rewind
    /// taken mid-brake followed by a second <c>HS</c> on the same target re-projects the bar from wherever the
    /// aircraft has got to — the ratchet — and a replay restored from that snapshot stops somewhere the live
    /// run never did.
    /// </summary>
    [Fact]
    public void PhaseRestore_ThenASecondHoldShort_LeavesTheMovedStopWhereItWas()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? junction = ground.Layout.FindIntersectionNode("B", "T");
        Assert.True(junction is not null, "SFO layout has no B/T junction");

        GroundNode? spawn = NodeEastOnB(ground.Layout, junction!, RunUpFt);
        if (spawn is null)
        {
            output.WriteLine("SFO layout offers no taxiway-B node east of the B/T junction to roll in from");
            return;
        }

        var heading = new TrueHeading(GeoMath.BearingTo(spawn.Position, junction!.Position));
        AircraftState aircraft = SfoGroundHarness.SpawnAt(ground, Callsign, Type, (spawn, heading), new HoldingInPositionPhase());
        CommandResult taxi = ground.Engine.SendCommand(Callsign, TaxiClearance);
        Assert.True(taxi.Success, $"{TaxiClearance} refused: {taxi.Message}");

        CommandResult? issued = null;
        for (int second = 1; (second <= 240) && (issued is null); second++)
        {
            ground.Engine.TickOneSecond();
            if ((DistanceFt(aircraft.Position, junction.Position) > IssueWithinFt) || (aircraft.GroundSpeed < IssueSpeedKts))
            {
                continue;
            }

            issued = ground.Engine.SendCommand(Callsign, "HS T");
            output.WriteLine($"HS T at {aircraft.GroundSpeed:F1} kt: {issued.Success} — {issued.Message}");
        }

        Assert.True(issued is not null, $"{Callsign} never reached the B/T bar at taxi speed");
        Assert.True(issued!.Success, $"HS T refused: {issued.Message}");
        Assert.Contains("Unable", issued.Message, StringComparison.OrdinalIgnoreCase);

        // One tick for the taxi phase to move the bar to the stop it can make.
        ground.Engine.TickOneSecond();
        HoldShortPoint bar = TheTBar(aircraft);
        Assert.True(bar.Unable, "the bar was never flagged unable");
        double movedLat = bar.Latitude!.Value;
        double movedLon = bar.Longitude!.Value;

        // The rewind: the running phase replaced by one restored from its own snapshot, as PhaseList does —
        // added without OnStart, so the next tick re-runs SetupCurrentSegment.
        PhaseList phases = aircraft.Phases ?? throw new InvalidOperationException($"{Callsign} has no phase list");
        Assert.True(
            phases.CurrentPhase is TaxiingPhase,
            $"the aircraft left the taxi before the rewind; phase {phases.CurrentPhase?.GetType().Name}"
        );
        var dto = (TaxiingPhaseDto)phases.CurrentPhase!.ToSnapshot();
        Assert.Equal(bar.NodeId, dto.UnableStopNodeId);
        phases.Phases[phases.CurrentIndex] = TaxiingPhase.FromSnapshot(dto);
        ground.Engine.TickOneSecond();

        Assert.True(
            phases.CurrentPhase is TaxiingPhase,
            $"the aircraft stopped before the second HS could reach the taxi; phase {phases.CurrentPhase?.GetType().Name}"
        );
        CommandResult reissued = ground.Engine.SendCommand(Callsign, "HS T");
        output.WriteLine($"HS T again at {aircraft.GroundSpeed:F1} kt: {reissued.Success} — {reissued.Message}");
        Assert.True(reissued.Success, $"the second HS T was refused: {reissued.Message}");
        ground.Engine.TickOneSecond();

        HoldShortPoint after = TheTBar(aircraft);
        output.WriteLine(
            $"stop moved {DistanceFt(new LatLon(movedLat, movedLon), new LatLon(after.Latitude!.Value, after.Longitude!.Value)):F1} ft by the second HS"
        );
        Assert.Equal(movedLat, after.Latitude!.Value, 9);
        Assert.Equal(movedLon, after.Longitude!.Value, 9);
    }

    /// <summary>The uncleared hold-short of T on the aircraft's route.</summary>
    /// <param name="aircraft">The taxiing aircraft.</param>
    /// <returns>The bar.</returns>
    private static HoldShortPoint TheTBar(AircraftState aircraft)
    {
        TaxiRoute route = aircraft.Ground.AssignedTaxiRoute ?? throw new InvalidOperationException($"{aircraft.Callsign} has no taxi route");
        HoldShortPoint? bar = route.HoldShortPoints.FirstOrDefault(h =>
            !h.IsCleared && string.Equals(h.TargetName, "T", StringComparison.OrdinalIgnoreCase)
        );
        Assert.True(bar is not null, "the route carries no uncleared hold-short of T");
        Assert.True((bar!.Latitude is not null) && (bar.Longitude is not null), "the hold-short of T has no computed position");
        return bar;
    }

    /// <summary>
    /// The node on taxiway B nearest <paramref name="wantFt"/> east of the junction, by along-track distance
    /// on B's east bearing — the fillet arcs at each junction carry joined names, so an edge walk stops at
    /// the first corner.
    /// </summary>
    /// <param name="layout">SFO ground layout.</param>
    /// <param name="junction">The B/T junction.</param>
    /// <param name="wantFt">How far east of it to start, in feet.</param>
    /// <returns>The spawn node, or null when B runs nowhere east of the junction.</returns>
    private static GroundNode? NodeEastOnB(AirportGroundLayout layout, GroundNode junction, double wantFt)
    {
        const double MaxCrossTrackFt = 200.0;

        double eastBearing = 0;
        foreach (IGroundEdge edge in junction.Edges)
        {
            if (edge.MatchesTaxiway("B") && (edge.OtherNode(junction).Position.Lon > junction.Position.Lon))
            {
                eastBearing = GeoMath.BearingTo(junction.Position, edge.OtherNode(junction).Position);
                break;
            }
        }

        GroundNode? best = null;
        double bestScore = double.MaxValue;
        foreach (GroundNode node in layout.GetNodesOnTaxiway("B"))
        {
            double distFt = DistanceFt(junction.Position, node.Position);
            double deltaDeg = GeoMath.AbsBearingDifference(eastBearing, GeoMath.BearingTo(junction.Position, node.Position));
            double alongFt = distFt * Math.Cos(deltaDeg * Math.PI / 180.0);
            double crossFt = Math.Abs(distFt * Math.Sin(deltaDeg * Math.PI / 180.0));
            if ((alongFt <= 0) || (crossFt > MaxCrossTrackFt) || (Math.Abs(alongFt - wantFt) >= bestScore))
            {
                continue;
            }

            best = node;
            bestScore = Math.Abs(alongFt - wantFt);
        }

        return best;
    }

    /// <summary>
    /// A one-segment route down a real taxiway edge long enough to set a hold-short back along, and the node
    /// the bar protects. Null when the layout offers no such edge.
    /// </summary>
    /// <param name="layout">Ground layout to pick the edge from.</param>
    /// <returns>The route and the hold-short node id.</returns>
    private static (TaxiRoute Route, int NodeId)? BuildOneSegmentRoute(AirportGroundLayout layout)
    {
        foreach (GroundNode node in layout.Nodes.Values.OrderBy(n => n.Id))
        {
            if (node.Id < 0)
            {
                continue;
            }

            foreach (IGroundEdge edge in node.Edges)
            {
                GroundNode other = edge.OtherNode(node);
                if ((edge is not GroundEdge) || (other.Id < 0) || (other.Id == node.Id) || ((edge.DistanceNm * GeoMath.FeetPerNm) < MinEdgeFt))
                {
                    continue;
                }

                var route = new TaxiRoute
                {
                    Segments = [new TaxiRouteSegment { TaxiwayName = edge.TaxiwayName, Edge = edge.Directed(other, node) }],
                    HoldShortPoints = [],
                    CurrentSegmentIndex = 0,
                };
                return (route, node.Id);
            }
        }

        return null;
    }
}
