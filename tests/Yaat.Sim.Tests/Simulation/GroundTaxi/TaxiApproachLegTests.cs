using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Unit-level cover for the free-space approach leg that bridges an aircraft's live position to the node
/// its resolved taxi route starts at. After a plain <c>PUSH</c> off OAK gate 15 the aircraft rests ~105 ft
/// ahead of the route's start node (763, the <c>T - RAMP</c> fillet arc's from-node), so the route as
/// resolved begins somewhere the aircraft is not.
///
/// <para>The leg is apron transit at the pilot's discretion (7110.65 §3-7-2 NOTE 2; AIM 4-3-20.g.7) and adds
/// nothing to the clearance readback. A landing rollout gets one too, along the runway it is already on, to
/// the exit fillet ahead. The negative cases are the four shapes that must NOT get one: an aircraft already
/// at the node, an aircraft whose route starts at a runway holding position, an aircraft that has already
/// driven past the start node with the route continuing ahead, and an aircraft whose line to the node
/// crosses a runway.</para>
/// </summary>
public class TaxiApproachLegTests(ITestOutputHelper output)
{
    private const string Callsign = "DAL2150";
    private const string AircraftType = "A319";

    /// <summary>Resting pose after the plain PUSH, from the recorded session.</summary>
    private const double PushedLat = 37.710217680439534;
    private const double PushedLon = -122.21728593336832;
    private const double PushedHeadingDeg = 53.0;

    /// <summary>The route's start node — the from-node of the <c>T - RAMP</c> fillet arc.</summary>
    private const int StartNodeId = 763;

    /// <summary>OAK W1's runway 30 holding position.</summary>
    private const int HoldShortNodeId = 495;

    /// <summary>How far past a holding position to look for the runway it protects.</summary>
    private const double RunwayProbeFt = 400.0;

    /// <summary>Where an aircraft waiting at that holding position sits, back along its taxiway.</summary>
    private const double HoldShortStandoffFt = 30.0;

    /// <summary>How far past a node an aircraft that has already driven through it sits.</summary>
    private const double PastNodeStandoffFt = 30.0;

    /// <summary>A straight taxiway T pair: 153 is the node the route would start at, 766 the next one along.</summary>
    private const int StraightFromNodeId = 153;
    private const int StraightToNodeId = 766;

    private AirportGroundLayout? LoadOakLayout()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new TestAirportGroundData().GetLayout("OAK");
    }

    private static AircraftState MakeAircraft(AirportGroundLayout layout, LatLon position, double headingDeg)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    private TaxiRoute Resolve(AirportGroundLayout layout, AircraftState aircraft, TaxiCommand taxi)
    {
        var result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);

        output.WriteLine($"result: {result.Message}");
        output.WriteLine($"route: {route.ToSummary()} ({route.Segments.Count} segments)");
        for (int i = 0; i < Math.Min(4, route.Segments.Count); i++)
        {
            var seg = route.Segments[i];
            output.WriteLine($"  [{i}] {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName}");
        }

        return route;
    }

    private static LatLon Project(LatLon from, double bearingDeg, double distanceFt)
    {
        var (lat, lon) = GeoMath.ProjectPointRaw(from.Lat, from.Lon, bearingDeg, distanceFt / GeoMath.FeetPerNm);
        return new LatLon(lat, lon);
    }

    /// <summary>The pushed-off-the-gate case: the route gets a free-space leg from the aircraft to node 763.</summary>
    [Fact]
    public void PushedOffGate_PrependsFreeSpaceLegToTheStartNode()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var aircraft = MakeAircraft(layout, new LatLon(PushedLat, PushedLon), PushedHeadingDeg);
        var route = Resolve(layout, aircraft, new TaxiCommand(Path: ["U", "W"], HoldShorts: [], DestinationRunway: "30"));

        var leg = route.Segments[0];
        double legStartOffsetFt = GeoMath.DistanceNm(leg.Edge.FromNode.Position, aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"leg starts {legStartOffsetFt:F2} ft from the aircraft, {leg.Edge.DistanceNm * GeoMath.FeetPerNm:F0} ft long");

        Assert.True(leg.FromNodeId < 0, $"first segment starts at layout node {leg.FromNodeId}, not at a virtual node on the aircraft");
        Assert.Equal(StartNodeId, leg.ToNodeId);
        Assert.Equal("RAMP", leg.TaxiwayName);
        Assert.True(legStartOffsetFt <= 1.0, $"leg starts {legStartOffsetFt:F2} ft from the aircraft, not at it");
    }

    /// <summary>The leg is apron transit, so neither the taxiway sequence nor the readback mentions it.</summary>
    [Fact]
    public void PushedOffGate_LegIsInvisibleInTheReadback()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        var aircraft = MakeAircraft(layout, new LatLon(PushedLat, PushedLon), PushedHeadingDeg);
        var taxi = new TaxiCommand(Path: ["U", "W"], HoldShorts: [], DestinationRunway: "30");
        var result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"result: {result.Message}");
        output.WriteLine($"sequence: {route.FormatTaxiwaySequence()}");

        Assert.Equal("T U W W1", route.FormatTaxiwaySequence());
        Assert.Equal("Taxi via T U W W1 RWY 30 [taxiing via T — not in the route issued]", result.Message);
    }

    /// <summary>An aircraft standing on the start node has nothing to bridge.</summary>
    [Fact]
    public void StandingOnTheStartNode_AddsNoLeg()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(StartNodeId, out var startNode), $"node {StartNodeId} missing from the OAK layout");

        var aircraft = MakeAircraft(layout, startNode.Position, PushedHeadingDeg);
        var route = Resolve(layout, aircraft, new TaxiCommand(Path: ["U", "W"], HoldShorts: [], DestinationRunway: "30"));

        Assert.Equal(StartNodeId, route.Segments[0].FromNodeId);
    }

    /// <summary>
    /// A route that starts at a runway holding position gets no leg. An aircraft waiting at one sits half a
    /// fuselage behind the bar node, and driving it up to the node would put its nose past the holding-position
    /// marking (AIM 2-3-5.a.1); the hold itself also needs the real node id, not a virtual one.
    /// </summary>
    [Fact]
    public void RouteStartingAtARunwayHoldingPosition_AddsNoLeg()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(HoldShortNodeId, out var bar), $"node {HoldShortNodeId} missing from the OAK layout");
        Assert.Equal(GroundNodeType.RunwayHoldShort, bar.Type);

        GroundNode? runwaySide = null;
        foreach (var edge in bar.Edges)
        {
            var other = edge.OtherNode(bar);
            double bearing = GeoMath.BearingTo(bar.Position, other.Position);
            double ft = GeoMath.DistanceNm(bar.Position, other.Position) * GeoMath.FeetPerNm;

            // The runway side of a holding position is the direction in which the pavement ahead crosses a
            // runway centerline; the other side is the taxiway the aircraft waits on.
            bool towardRunway = layout.RunwayCenterlineBetween(bar.Position, Project(bar.Position, bearing, RunwayProbeFt));
            output.WriteLine(
                $"  node {bar.Id} edge on {edge.TaxiwayName} -> {other.Id} ({other.Type}) "
                    + $"bearing {bearing:F0} dist {ft:F0} ft towardRunway={towardRunway}"
            );
            if (towardRunway && (runwaySide is null))
            {
                runwaySide = other;
            }
        }

        Assert.NotNull(runwaySide);

        // W1's own waiting node sits 28 ft behind the bar, so a waiting aircraft resolves to that node rather
        // than the bar. Approach the bar from the runway side instead: the route then starts AT the holding
        // position with the aircraft short of it and pointing at it — every other guard passes, and only the
        // holding-position guard can refuse the leg.
        double runwayBearing = GeoMath.BearingTo(bar.Position, runwaySide.Position);
        var position = Project(bar.Position, runwayBearing, HoldShortStandoffFt);
        double headingDeg = GeoMath.BearingTo(position, bar.Position);
        output.WriteLine($"aircraft {HoldShortStandoffFt:F0} ft from the bar on bearing {runwayBearing:F0}, facing {headingDeg:F0}");

        var aircraft = MakeAircraft(layout, position, headingDeg);
        var route = Resolve(layout, aircraft, new TaxiCommand(Path: ["W1", "W"], HoldShorts: [], DestinationRunway: null));

        Assert.Equal(HoldShortNodeId, route.Segments[0].FromNodeId);
    }

    private static bool IsRunwayName(string name) => name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An aircraft that has already rolled past the start node, with the route continuing ahead, must not be
    /// sent back to it — pure pursuit converges onto the line from where it is.
    ///
    /// <para>Driven through <see cref="TaxiApproachLeg.Prepend"/> directly: from this pose
    /// <see cref="AirportGroundLayout.FindNearestNodeForTaxi"/> rejects the node behind and resolves the route
    /// from node 766 ahead instead, so the shape only reaches the leg through the plain nearest-node
    /// fallback.</para>
    /// </summary>
    [Fact]
    public void PastTheStartNodeWithTheRouteAhead_AddsNoLeg()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(StraightFromNodeId, out var from), $"node {StraightFromNodeId} missing from the OAK layout");
        Assert.True(layout.Nodes.TryGetValue(StraightToNodeId, out var to), $"node {StraightToNodeId} missing from the OAK layout");

        var alongEdge = from.Edges.FirstOrDefault(e => e.OtherNode(from).Id == StraightToNodeId);
        Assert.NotNull(alongEdge);

        double alongBearing = GeoMath.BearingTo(from.Position, to.Position);
        var position = Project(from.Position, alongBearing, PastNodeStandoffFt);
        output.WriteLine(
            $"aircraft {PastNodeStandoffFt:F0} ft past node {StraightFromNodeId} toward {StraightToNodeId} " + $"on bearing {alongBearing:F0}"
        );

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = alongEdge.TaxiwayName, Edge = alongEdge.Directed(from, to) }],
            HoldShortPoints = [],
        };

        var result = TaxiApproachLeg.Prepend(layout, position, new TrueHeading(alongBearing), route);

        Assert.Same(route, result);
    }

    /// <summary>Node 1144 — the from-node of OAK taxiway G's 28R exit fillet, on the runway centerline.</summary>
    private const int RunwayExitNodeId = 1144;

    /// <summary>The exit fillet's departure bearing: runway 28R's landing direction at that node.</summary>
    private const double Runway28RHeadingDeg = 292.0;

    /// <summary>How far back along the runway the rolling-out aircraft still is — a rollout is far from its exit.</summary>
    private const double RolloutBehindExitFt = 300.0;

    /// <summary>Node 1142 — the far (north) end of that same exit fillet, off the runway on taxiway G.</summary>
    private const int AcrossRunwayNodeId = 1142;

    /// <summary>How far back along 28R the apron-side aircraft sits, so its line to that node crosses the runway obliquely.</summary>
    private const double AcrossRunwayAlongFt = 150.0;

    /// <summary>How far to the south side of 28R it sits — clear of the pavement, inside the apron-length bound.</summary>
    private const double AcrossRunwayOffsetFt = 200.0;

    /// <summary>
    /// A landing rollout's taxi route starts at the runway-exit fillet, which is still hundreds of feet ahead
    /// of an aircraft rolling out. That drive runs ALONG the runway the aircraft is already on, so neither the
    /// apron-length bound nor the centerline-crossing refusal applies — the aircraft is not crossing a runway,
    /// it is on one. Without the leg the navigator's arc playback writes it the whole way onto the fillet in a
    /// single tick (the issue-213 N655EX 314 ft snap off OAK 28R).
    /// </summary>
    [Fact]
    public void RollingOutTowardTheRunwayExit_PrependsAnAlongRunwayLeg()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(RunwayExitNodeId, out var exit), $"node {RunwayExitNodeId} missing from the OAK layout");

        var position = Project(exit.Position, (Runway28RHeadingDeg + 180.0) % 360.0, RolloutBehindExitFt);
        output.WriteLine($"aircraft {RolloutBehindExitFt:F0} ft back along 28R from node {RunwayExitNodeId}, facing {Runway28RHeadingDeg:F0}");

        var aircraft = MakeAircraft(layout, position, Runway28RHeadingDeg);
        var route = Resolve(layout, aircraft, new TaxiCommand(Path: ["G", "D", "J"], HoldShorts: [], DestinationRunway: null));

        var leg = route.Segments[0];
        double legStartOffsetFt = GeoMath.DistanceNm(leg.Edge.FromNode.Position, aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"leg starts {legStartOffsetFt:F2} ft from the aircraft, {leg.Edge.DistanceNm * GeoMath.FeetPerNm:F0} ft long");

        Assert.True(leg.FromNodeId < 0, $"first segment starts at layout node {leg.FromNodeId}, not at a virtual node on the aircraft");
        Assert.Equal(RunwayExitNodeId, leg.ToNodeId);
        Assert.True(legStartOffsetFt <= 1.0, $"leg starts {legStartOffsetFt:F2} ft from the aircraft, not at it");
    }

    /// <summary>
    /// The along-runway allowance must not open the general case: an aircraft whose line to the route's start
    /// node crosses a runway still gets no leg. A free-space leg follows no painted line and is not
    /// obstacle-aware, so it may never be used to drive an aircraft across an active runway.
    /// </summary>
    [Fact]
    public void ApronLineCrossingARunway_AddsNoLeg()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(AcrossRunwayNodeId, out var node), $"node {AcrossRunwayNodeId} missing from the OAK layout");
        Assert.True(layout.Nodes.TryGetValue(RunwayExitNodeId, out var exit), $"node {RunwayExitNodeId} missing from the OAK layout");
        Assert.NotEqual(GroundNodeType.RunwayHoldShort, node.Type);

        var onward = node
            .Edges.Where(e => e is not GroundArc)
            .OrderByDescending(e => GeoMath.DistanceNm(node.Position, e.OtherNode(node).Position))
            .FirstOrDefault();
        Assert.NotNull(onward);

        var onwardNode = onward.OtherNode(node);

        // Back along 28R, then out to its south side: the line from there to the node north of the runway
        // crosses the centerline well away from any node, and the drive stays inside the apron-length bound.
        var alongRunway = Project(exit.Position, (Runway28RHeadingDeg + 180.0) % 360.0, AcrossRunwayAlongFt);
        var position = Project(alongRunway, (Runway28RHeadingDeg + 270.0) % 360.0, AcrossRunwayOffsetFt);
        double distFt = GeoMath.DistanceNm(position, node.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"aircraft {distFt:F0} ft from node {AcrossRunwayNodeId}, on the far side of 28R; "
                + $"route continues to node {onwardNode.Id} on {onward.TaxiwayName}"
        );

        Assert.True(
            layout.RunwayCenterlineBetween(position, node.Position),
            "the fixture must place the aircraft with a runway centerline between it and the route's start node"
        );

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = onward.TaxiwayName, Edge = onward.Directed(node, onwardNode) }],
            HoldShortPoints = [],
        };

        Assert.Same(route, TaxiApproachLeg.Prepend(layout, position, new TrueHeading(Runway28RHeadingDeg), route));
    }

    /// <summary>Node 805 — a real node on OAK taxiway B, 540 ft south of the 28R centerline and 2,460 ft back from node 1144.</summary>
    private const int ParallelTaxiwayNodeId = 805;

    /// <summary>A heading across 28R rather than along it: an aircraft on the pavement pointed at the far side.</summary>
    private const double AcrossRunwayHeadingDeg = 20.0;

    /// <summary>The along-runway bearing tolerance the fixtures are built against.</summary>
    private const double AlongRunwayToleranceDeg = 15.0;

    /// <summary>
    /// The one-segment route a rollout resolves to: OAK taxiway G's 28R exit fillet, leaving node 1144 for the
    /// taxiway side. Built directly rather than through <c>TryTaxi</c> so only the approach leg's own guards
    /// decide — a route resolved for an aircraft standing somewhere else starts at whatever node is nearest IT.
    /// </summary>
    private static TaxiRoute ExitFilletRoute(GroundNode exit)
    {
        var fillet = exit.Edges.FirstOrDefault(e => e.OtherNode(exit).Id == AcrossRunwayNodeId);
        Assert.NotNull(fillet);

        return new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = fillet.TaxiwayName, Edge = fillet.Directed(exit, fillet.OtherNode(exit)) }],
            HoldShortPoints = [],
        };
    }

    /// <summary>
    /// A parallel taxiway is not the runway. Bearing alone admitted an aircraft holding on OAK taxiway B — 540 ft
    /// south of the 28R centerline — because its line to node 1144, 2,460 ft up the field, still ran within 15° of
    /// the runway, and the along-runway exception then skipped both the length bound and the runway-crossing
    /// refusal. The leg it was given ran across B's holding position marking and onto the runway
    /// (AIM 4-3-18.a.5, AIM 2-3-5.a.1), so the exception must test that the aircraft is ON that runway.
    /// </summary>
    [Fact]
    public void OnAParallelTaxiwayAbeamTheRunway_AddsNoLeg()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(ParallelTaxiwayNodeId, out var abeam), $"node {ParallelTaxiwayNodeId} missing from the OAK layout");
        Assert.True(layout.Nodes.TryGetValue(RunwayExitNodeId, out var exit), $"node {RunwayExitNodeId} missing from the OAK layout");

        double bearingToExit = GeoMath.BearingTo(abeam.Position, exit.Position);
        double offRunwayDeg = GeoMath.AbsBearingDifference(bearingToExit, Runway28RHeadingDeg);
        double distFt = GeoMath.DistanceNm(abeam.Position, exit.Position) * GeoMath.FeetPerNm;
        double abeamFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(abeam.Position, exit.Position, new TrueHeading(Runway28RHeadingDeg))) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"node {ParallelTaxiwayNodeId} sits {abeamFt:F0} ft abeam the 28R centerline, {distFt:F0} ft from node {RunwayExitNodeId} "
                + $"on bearing {bearingToExit:F1} ({offRunwayDeg:F1}° off the runway)"
        );

        Assert.True(offRunwayDeg <= AlongRunwayToleranceDeg, $"the fixture must sit within {AlongRunwayToleranceDeg:F0}° of the runway bearing");
        Assert.True(abeamFt > 100.0, $"the fixture must sit off the runway pavement, but it is {abeamFt:F0} ft from the centerline");

        var route = ExitFilletRoute(exit);

        Assert.Same(route, TaxiApproachLeg.Prepend(layout, abeam.Position, new TrueHeading(Runway28RHeadingDeg), route));
    }

    /// <summary>
    /// Being on the pavement is not enough either: an aircraft stopped on the 28R centerline but pointed 20° —
    /// across the runway, not down it — is not rolling out, and the free-space leg to the exit fillet ahead is a
    /// drive it is not already making. With the along-runway exception refused, the ordinary apron rules apply and
    /// the runway centerline between the aircraft and the node refuses the leg.
    /// </summary>
    [Fact]
    public void OnTheRunwayButHeadingAcrossIt_AddsNoLeg()
    {
        var layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(RunwayExitNodeId, out var exit), $"node {RunwayExitNodeId} missing from the OAK layout");

        var position = Project(exit.Position, (Runway28RHeadingDeg + 180.0) % 360.0, RolloutBehindExitFt);
        double bearingToExit = GeoMath.BearingTo(position, exit.Position);
        output.WriteLine(
            $"aircraft {RolloutBehindExitFt:F0} ft back along 28R from node {RunwayExitNodeId} (bearing {bearingToExit:F1}), "
                + $"facing {AcrossRunwayHeadingDeg:F0} — across the runway, not along it"
        );

        var route = ExitFilletRoute(exit);

        Assert.Same(route, TaxiApproachLeg.Prepend(layout, position, new TrueHeading(AcrossRunwayHeadingDeg), route));
    }
}
