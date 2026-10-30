using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// Unit-level cover for the free-space approach leg that bridges an aircraft's live position to the node
/// its resolved taxi route starts at. After a plain <c>PUSH</c> off OAK gate 15 the aircraft rests ~105 ft
/// ahead of the route's start node (the ramp end of a <c>T - RAMP</c> fillet arc), so the route as
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

    /// <summary>
    /// The route's start node: the from-node of the <c>T - RAMP</c> fillet arc, the ramp end of such an arc nearest the
    /// pushed aircraft (~105 ft ahead of it; the next is ~158 ft).
    /// </summary>
    internal static GroundNode StartNode(AirportGroundLayout layout) => NearestOnBoth(layout, "RAMP", "T - RAMP", new LatLon(PushedLat, PushedLon));

    /// <summary>
    /// The node with an edge named <paramref name="nameA"/> and one named <paramref name="nameB"/> nearest <paramref name="near"/>.
    /// </summary>
    private static GroundNode NearestOnBoth(AirportGroundLayout layout, string nameA, string nameB, LatLon near) =>
        layout
            .Nodes.Values.Where(node => HasEdgeNamed(node, nameA) && HasEdgeNamed(node, nameB))
            .MinBy(node => GeoMath.DistanceNm(node.Position, near))
        ?? throw new InvalidOperationException($"OAK has no node on both {nameA} and {nameB}");

    private static bool HasEdgeNamed(GroundNode node, string name) => node.Edges.Any(edge => edge.TaxiwayName == name);

    /// <summary>OAK W1's runway 30 holding position.</summary>
    private static int HoldShortNodeId(AirportGroundLayout layout) => GroundNodeLookup.HoldShortBar(layout, "30", "W1").Id;

    /// <summary>How far past a holding position to look for the runway it protects.</summary>
    private const double RunwayProbeFt = 400.0;

    /// <summary>Where an aircraft waiting at that holding position sits, back along its taxiway.</summary>
    private const double HoldShortStandoffFt = 30.0;

    /// <summary>How far past a node an aircraft that has already driven through it sits.</summary>
    private const double PastNodeStandoffFt = 30.0;

    /// <summary>
    /// A straight taxiway T pair: 153, a T/RAMP junction, is the node the route would start at, and 765 the next T node
    /// south-east along, where three <c>T - RAMP</c> fillet arcs meet. Pinned by id: no lookup singles the pair out
    /// (OAK has 11 T/RAMP junctions and 22 nodes on both T and a <c>T - RAMP</c> arc), and the tests below check the two
    /// share an edge.
    /// </summary>
    private const int StraightFromNodeId = 153;
    private const int StraightToNodeId = 765;

    private AirportGroundLayout? LoadOakLayout()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new TestAirportGroundData().GetLayout("OAK");
    }

    /// <summary>
    /// How far behind a runway bar the aircraft is, for the approach leg's painted-stop test, is measured along the taxi edge it
    /// is on, not along the route's first leg: OAK W1's runway 30 bar and its approach edge (the W1 edge whose other end lies
    /// farther from the runway), with a position 72 ft before the bar along that edge.
    /// </summary>
    [Fact]
    public void BehindBarAlongEdgeFt_MeasuresAlongTheOccupiedEdge_NotTheFirstLeg()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        GroundNode bar = GroundNodeLookup.HoldShortBar(layout, "30", "W1");
        RunwayRectangle rect = RunwayCrossingDetector.BuildRunwayRectangle(layout.Runways.Single(runway => runway.Id.Equals(bar.RunwayId)));
        GroundEdge approach =
            bar.Edges.OfType<GroundEdge>()
                .Where(edge => edge.MatchesTaxiway("W1"))
                .MaxBy(edge =>
                    Math.Abs(GeoMath.SignedCrossTrackDistanceNm(edge.OtherNode(bar).Position, new LatLon(rect.RefLat, rect.RefLon), rect.TrueHeading))
                )
            ?? throw new InvalidOperationException($"OAK runway 30 bar #{bar.Id} has no W1 edge");
        var awayFromTheBar = new TrueHeading(GeoMath.BearingTo(bar.Position, approach.OtherNode(bar).Position));
        LatLon position = GeoMath.ProjectPoint(bar.Position, awayFromTheBar, 72.0 / GeoMath.FeetPerNm);

        double behindFt = TaxiApproachLeg.BehindBarAlongEdgeFt(position, bar, approach);

        output.WriteLine($"bar #{bar.Id}, approach edge to #{approach.OtherNode(bar).Id}: {behindFt:F2} ft behind");
        Assert.InRange(behindFt, 71.5, 72.5);
    }

    private static AircraftState MakeAircraft(AirportGroundLayout layout, LatLon position, double headingDeg)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = position,
            TrueHeading = new TrueHeading(headingDeg),
            Declination = MagneticDeclination.GetDeclination(position),
            Altitude = 6,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "LAX" },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    private TaxiRoute Resolve(AirportGroundLayout layout, AircraftState aircraft, TaxiCommand taxi)
    {
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        Assert.NotEmpty(route.Segments);

        output.WriteLine($"result: {result.Message}");
        output.WriteLine($"route: {route.ToSummary()} ({route.Segments.Count} segments)");
        for (int i = 0; i < Math.Min(4, route.Segments.Count); i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            output.WriteLine($"  [{i}] {seg.FromNodeId} -> {seg.ToNodeId} on {seg.TaxiwayName}");
        }

        return route;
    }

    private static LatLon Project(LatLon from, double bearingDeg, double distanceFt)
    {
        (double lat, double lon) = GeoMath.ProjectPointRaw(from.Lat, from.Lon, bearingDeg, distanceFt / GeoMath.FeetPerNm);
        return new LatLon(lat, lon);
    }

    /// <summary>The pushed-off-the-gate case: the route gets a free-space leg from the aircraft to its start node.</summary>
    [Fact]
    public void PushedOffGate_PrependsFreeSpaceLegToTheStartNode()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft(layout, new LatLon(PushedLat, PushedLon), PushedHeadingDeg);
        TaxiRoute route = Resolve(layout, aircraft, new TaxiCommand(Path: ["U", "W"], HoldShorts: [], Expedite: false, DestinationRunway: "30"));

        TaxiRouteSegment leg = route.Segments[0];
        double legStartOffsetFt = GeoMath.DistanceNm(leg.Edge.FromNode.Position, aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"leg starts {legStartOffsetFt:F2} ft from the aircraft, {leg.Edge.DistanceNm * GeoMath.FeetPerNm:F0} ft long");

        Assert.True(leg.FromNodeId < 0, $"first segment starts at layout node {leg.FromNodeId}, not at a virtual node on the aircraft");
        Assert.Equal(StartNode(layout).Id, leg.ToNodeId);
        Assert.Equal("RAMP", leg.TaxiwayName);
        Assert.True(legStartOffsetFt <= 1.0, $"leg starts {legStartOffsetFt:F2} ft from the aircraft, not at it");
    }

    /// <summary>The leg is apron transit, so neither the taxiway sequence nor the readback mentions it.</summary>
    [Fact]
    public void PushedOffGate_LegIsInvisibleInTheReadback()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft(layout, new LatLon(PushedLat, PushedLon), PushedHeadingDeg);
        var taxi = new TaxiCommand(Path: ["U", "W"], HoldShorts: [], Expedite: false, DestinationRunway: "30");
        CommandResult result = GroundCommandHandler.TryTaxi(aircraft, taxi, layout);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"result: {result.Message}");
        output.WriteLine($"sequence: {route.FormatTaxiwaySequence()}");

        Assert.Equal("T U W W1", route.FormatTaxiwaySequence());
        Assert.Equal(
            "Taxi via U W RWY 30 [taxiing via T — not in the route issued] "
                + "(DAL2150 pushed facing 040 and turns about to taxi; PUSH FACE SW would have faced the route)",
            result.Message
        );
    }

    /// <summary>
    /// The same pushback, with the aircraft known to be on T: following T onto U is taxiing along the taxiway it
    /// occupies, not a deviation from the route issued, so no note names T.
    /// </summary>
    [Fact]
    public void PushedOntoT_OccupyingT_NoNoteForT()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft(layout, new LatLon(PushedLat, PushedLon), PushedHeadingDeg);
        aircraft.Ground.CurrentTaxiway = "T";
        CommandResult result = GroundCommandHandler.TryTaxi(
            aircraft,
            new TaxiCommand(Path: ["U", "W"], HoldShorts: [], Expedite: false, DestinationRunway: "30"),
            layout
        );
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"result: {result.Message}");

        Assert.Equal("T U W W1", route.FormatTaxiwaySequence());
        // The pose rests 164 ft off T's nearest edge, so the warning names no lane.
        Assert.Equal(
            "Taxi via T U W RWY 30 (DAL2150 pushed facing 040 and turns about to taxi; PUSH FACE SW would have faced the route)",
            result.Message
        );
    }

    /// <summary>
    /// The same pushback with a stale current taxiway (W, which the start node does not lie on): the aircraft is not
    /// on the taxiway the route bridges along, so the note for T stays.
    /// </summary>
    [Fact]
    public void PushedOntoT_StaleCurrentTaxiway_KeepsNoteForT()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        AircraftState aircraft = MakeAircraft(layout, new LatLon(PushedLat, PushedLon), PushedHeadingDeg);
        aircraft.Ground.CurrentTaxiway = "W";
        CommandResult result = GroundCommandHandler.TryTaxi(
            aircraft,
            new TaxiCommand(Path: ["U", "W"], HoldShorts: [], Expedite: false, DestinationRunway: "30"),
            layout
        );
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");
        output.WriteLine($"result: {result.Message}");

        Assert.Equal(
            "Taxi via U W RWY 30 [taxiing via T — not in the route issued] "
                + "(DAL2150 pushed facing 040 and turns about to taxi; PUSH FACE SW would have faced the route)",
            result.Message
        );
    }

    /// <summary>
    /// An SFO aircraft on F, 40 ft short of the F/A junction (node 54, ahead), nosed 298° towards it: mid-way along F
    /// edge 54–1598, whose far end 1598 lies on E.
    /// </summary>
    private static readonly LatLon SfoOnFShortOfA = new(37.61906270023921, -122.38060898289163);

    private const double SfoOnFShortOfAHeadingDeg = 297.9;

    /// <summary>Where the F edge from SFO's F/A junction meets E.</summary>
    private const int SfoFAtENodeId = 1598;

    /// <summary>
    /// A stale current taxiway that names the very taxiway the route bridges along: an SFO aircraft standing at the F/A
    /// junction (node 54), nosed down F towards E, cleared <c>TAXI B</c>, bridges F → E → B, and its current taxiway still
    /// reads E although the start node does not lie on E. It is not on E, so the note for E stays.
    /// </summary>
    [Fact]
    public void BridgesAlongE_StaleCurrentTaxiwayE_KeepsNoteForE()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        GroundNode junction = GroundNodeLookup.Junction(ground.Layout, "F", "A");
        double downFDeg = GeoMath.BearingTo(junction.Position, ground.Layout.Nodes[SfoFAtENodeId].Position);
        AircraftState aircraft = MakeAircraft(ground.Layout, junction.Position, downFDeg);
        aircraft.Ground.CurrentTaxiway = "E";
        GroundNode? start = ground.Layout.FindTaxiStartNode(aircraft.Position, aircraft.TrueHeading, TaxiStartTraffic.Of(aircraft, []));
        Assert.NotNull(start);
        Assert.DoesNotContain(start.Edges, e => e.MatchesTaxiway("E"));

        CommandResult result = GroundCommandHandler.TryTaxi(
            aircraft,
            new TaxiCommand(Path: ["B"], HoldShorts: [], Expedite: false, DestinationRunway: null),
            ground.Layout
        );
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");
        output.WriteLine($"start {start.Id}; result: {result.Message}");

        Assert.Equal("F E B", aircraft.Ground.AssignedTaxiRoute?.FormatTaxiwaySequence());
        Assert.Contains("taxiing via E — not in the route issued", result.Message);
    }

    /// <summary>
    /// A C172 on F 40 ft short of node 54, nosed away from E, cleared <c>TAXI B</c>: the route from node 54 ahead would
    /// drive back along F past the aircraft to reach E, so the taxi starts at F's far end, node 1598 on E. The aircraft
    /// turns about and taxis E then B. It occupies F, not its stale current taxiway E, so E is noted as not issued.
    /// </summary>
    [Fact]
    public void StaleE_OnF_RouteBehind_StartsAtFarEndOnE()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        CommandResult result = TaxiBFromFShortOfA(ground.Layout, "C172", out AircraftState aircraft);
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");

        Assert.Equal("E B", aircraft.Ground.AssignedTaxiRoute?.FormatTaxiwaySequence());
        Assert.Equal(
            "Taxi via B [taxiing via E — not in the route issued] "
                + "(DAL2150 pushed facing 285 and turns about to taxi; PUSH FACE E would have faced the route)",
            result.Message
        );
    }

    /// <summary>
    /// The same pose in a B738: its gear does not fit a turn about on F, but it is holding after a pushback, and an
    /// aircraft held after a pushback is never refused a turn about, so it turns about and taxis E then B like the C172.
    /// </summary>
    [Fact]
    public void StaleE_OnF_RouteBehind_JetHeldAfterPushTurnsAbout()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        CommandResult result = TaxiBFromFShortOfA(ground.Layout, "B738", out AircraftState aircraft);

        Assert.True(result.Success, $"TryTaxi was refused: {result.Message}");
        Assert.Equal("E B", aircraft.Ground.AssignedTaxiRoute?.FormatTaxiwaySequence());
        Assert.Equal(
            "Taxi via B [taxiing via E — not in the route issued] "
                + "(DAL2150 pushed facing 285 and turns about to taxi; PUSH FACE E would have faced the route)",
            result.Message
        );
    }

    /// <summary>How far along F from node 1608, F's far end from the F/A junction, the C172 stands when it turns about toward 1607.</summary>
    private const double TurnsAboutFromFarEndFt = 30.0;

    /// <summary>How far along F from node 1608 the C172 stands when its route leaves ahead through the F/A junction.</summary>
    private const double RouteAheadFt = 45.0;

    /// <summary>
    /// A C172 held after a push on F, 30 ft from node 1608 and nosed along F toward the F/A junction (node 54), cleared
    /// <c>TAXI B</c>: the start logic turns it about toward 1608, behind it, so the RPO is warned on the bearing to that target.
    /// </summary>
    [Fact]
    public void OnF_TurnsAboutTowardTheFarEnd_Warns()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        (CommandResult result, TaxiRoute route) = TaxiBAlongFTowardA(ground.Layout, TurnsAboutFromFarEndFt);

        Assert.Equal(TaxiTurnAboutShape.FromFarEnd, route.TurnAboutShape);
        Assert.Equal(SfoFFarEndNodeId, route.TurnAboutTargetNodeId);
        Assert.Equal(
            "Taxi via B [taxiing via E — not in the route issued] "
                + "(DAL2150 pushed facing 285 and turns about to taxi; PUSH FACE E would have faced the route)",
            result.Message
        );
    }

    /// <summary>
    /// The same C172 45 ft from node 1608: its route leaves ahead through the F/A junction, so it does not turn about and is not
    /// warned.
    /// </summary>
    [Fact]
    public void OnF_RouteAheadThroughTheJunction_DoesNotWarn()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        (CommandResult result, TaxiRoute route) = TaxiBAlongFTowardA(ground.Layout, RouteAheadFt);

        Assert.Equal(TaxiTurnAboutShape.None, route.TurnAboutShape);
        Assert.DoesNotContain("turns about to taxi", result.Message);
    }

    /// <summary>SFO F's far end from the F/A junction along the edge the stale-E pose stands on.</summary>
    private const int SfoFFarEndNodeId = 1608;

    /// <summary>
    /// A C172 held after a push on F edge 54–1608, <paramref name="fromFarEndFt"/> from 1608, nosed toward 54, cleared <c>TAXI B</c>.
    /// </summary>
    private (CommandResult Result, TaxiRoute Route) TaxiBAlongFTowardA(AirportGroundLayout layout, double fromFarEndFt)
    {
        GroundNode farEnd = layout.Nodes[SfoFFarEndNodeId];
        double towardA = GeoMath.BearingTo(farEnd.Position, GroundNodeLookup.Junction(layout, "F", "A").Position);
        AircraftState aircraft = MakeAircraft(layout, Project(farEnd.Position, towardA, fromFarEndFt), towardA);
        aircraft.AircraftType = "C172";
        CommandResult result = GroundCommandHandler.TryTaxi(
            aircraft,
            new TaxiCommand(Path: ["B"], HoldShorts: [], Expedite: false, DestinationRunway: null),
            layout
        );
        Assert.True(result.Success, $"TryTaxi failed: {result.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"{fromFarEndFt:F0} ft: {result.Message}; turn about {route.TurnAboutShape} to {route.TurnAboutTargetNodeId}");
        return (result, route);
    }

    /// <summary>A <paramref name="type"/> on F short of node 54, its current taxiway a stale E, cleared <c>TAXI B</c>.</summary>
    private CommandResult TaxiBFromFShortOfA(AirportGroundLayout layout, string type, out AircraftState aircraft)
    {
        aircraft = MakeAircraft(layout, SfoOnFShortOfA, SfoOnFShortOfAHeadingDeg);
        aircraft.AircraftType = type;
        aircraft.Ground.CurrentTaxiway = "E";
        CommandResult result = GroundCommandHandler.TryTaxi(
            aircraft,
            new TaxiCommand(Path: ["B"], HoldShorts: [], Expedite: false, DestinationRunway: null),
            layout
        );
        output.WriteLine($"{type}: {result.Success} — {result.Message}");
        return result;
    }

    /// <summary>An aircraft standing on the start node has nothing to bridge.</summary>
    [Fact]
    public void StandingOnTheStartNode_AddsNoLeg()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        GroundNode startNode = StartNode(layout);

        AircraftState aircraft = MakeAircraft(layout, startNode.Position, PushedHeadingDeg);
        TaxiRoute route = Resolve(layout, aircraft, new TaxiCommand(Path: ["U", "W"], HoldShorts: [], Expedite: false, DestinationRunway: "30"));

        Assert.Equal(startNode.Id, route.Segments[0].FromNodeId);
    }

    /// <summary>
    /// A route that starts at a runway holding position gets no leg. An aircraft waiting at one sits half a
    /// fuselage behind the bar node, and driving it up to the node would put its nose past the holding-position
    /// marking (AIM 2-3-5.a.1); the hold itself also needs the real node id, not a virtual one.
    /// </summary>
    [Fact]
    public void RouteStartingAtARunwayHoldingPosition_AddsNoLeg()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        GroundNode bar = layout.Nodes[HoldShortNodeId(layout)];
        Assert.Equal(GroundNodeType.RunwayHoldShort, bar.Type);

        GroundNode? runwaySide = null;
        foreach (IGroundEdge edge in bar.Edges)
        {
            GroundNode other = edge.OtherNode(bar);
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
        double standoffFt = GeoMath.DistanceNm(bar.Position, runwaySide.Position) * GeoMath.FeetPerNm / 5.0;
        LatLon position = Project(bar.Position, runwayBearing, standoffFt);
        double headingDeg = GeoMath.BearingTo(position, bar.Position);
        output.WriteLine($"aircraft {standoffFt:F1} ft from the bar on bearing {runwayBearing:F0}, facing {headingDeg:F0}");

        AircraftState aircraft = MakeAircraft(layout, position, headingDeg);
        TaxiRoute route = Resolve(layout, aircraft, new TaxiCommand(Path: ["W1", "W"], HoldShorts: [], Expedite: false, DestinationRunway: null));

        Assert.Equal(HoldShortNodeId(layout), route.Segments[0].FromNodeId);
    }

    /// <summary>The type the runway-bar rows stage: a B738's painted stop sits half its fuselage plus the set-back margin from the bar.</summary>
    private const string BarAircraftType = "B738";

    /// <summary>How far beyond the painted stop the rows that stand clear of it are placed.</summary>
    private const double ClearOfThePaintedStopFt = 10.0;

    private static double BarAircraftLengthFt => AircraftLength.ResolveFt(BarAircraftType);

    private static double PaintedStopFt => (BarAircraftLengthFt / 2.0) + GroundNavigator.SetBackStopMarginFt;

    /// <summary>A runway bar, the node out along its straight approach edge, and the node on its runway side.</summary>
    private readonly record struct BarApproach(GroundNode Bar, GroundNode Outboard, GroundNode RunwaySide)
    {
        public double AwayDeg => GeoMath.BearingTo(Bar.Position, Outboard.Position);

        public double TowardRunwayDeg => GeoMath.BearingTo(Bar.Position, RunwaySide.Position);
    }

    /// <summary>
    /// OAK runway 33's bar on K at the end of K's longest straight approach edge, the K node out along that edge, and the
    /// straight-edge neighbour on its runway side (the one nearer the runway centreline).
    /// </summary>
    private static BarApproach K33Bar(AirportGroundLayout layout)
    {
        GroundRunway runway = layout.Runways.Single(r => r.Id.Contains("33"));
        (GroundNode bar, GroundNode outboard) = layout
            .GetRunwayHoldShortNodes("33")
            .Where(node => node.IsShortBar == false)
            .SelectMany(node =>
                node.Edges.OfType<GroundEdge>()
                    .Where(edge =>
                        edge.MatchesTaxiway("K") && (CentrelineFt(runway, edge.OtherNode(node).Position) > CentrelineFt(runway, node.Position))
                    )
                    .Select(edge => (Bar: node, Outboard: edge.OtherNode(node)))
            )
            .MaxBy(pair => GeoMath.DistanceNm(pair.Bar.Position, pair.Outboard.Position));
        Assert.NotNull(bar);

        GroundNode? runwaySide = bar
            .Edges.OfType<GroundEdge>()
            .Select(edge => edge.OtherNode(bar))
            .FirstOrDefault(node => CentrelineFt(runway, node.Position) < CentrelineFt(runway, bar.Position));
        Assert.NotNull(runwaySide);
        return new BarApproach(bar, outboard, runwaySide);
    }

    /// <summary>The distance (ft) from <paramref name="point"/> to <paramref name="runway"/>'s centreline, as an infinite line.</summary>
    private static double CentrelineFt(GroundRunway runway, LatLon point)
    {
        var axisStart = new LatLon(runway.Coordinates[0].Lat, runway.Coordinates[0].Lon);
        var axisEnd = new LatLon(runway.Coordinates[^1].Lat, runway.Coordinates[^1].Lon);
        var axis = new TrueHeading(GeoMath.BearingTo(axisStart, axisEnd));
        return Math.Abs(GeoMath.SignedCrossTrackDistanceNm(point, axisStart, axis)) * GeoMath.FeetPerNm;
    }

    /// <summary>How far abeam the runway-side stub's line the past-the-bar row stands: inside the on-edge offset.</summary>
    private const double StubAbeamFt = 15.0;

    /// <summary>How much of the stub is left ahead of the past-the-bar row: less than twice <see cref="StubAbeamFt"/>.</summary>
    private const double StubLeftAheadFt = 20.0;

    /// <summary>The shortest runway-side stub the runway-side rows can stand on with <see cref="StubLeftAheadFt"/> to spare.</summary>
    private static double MinStubFt => PaintedStopFt + ClearOfThePaintedStopFt + StubLeftAheadFt;

    /// <summary>
    /// Where the past-the-bar row stands: on the stub, <see cref="StubLeftAheadFt"/> short of its far end and
    /// <see cref="StubAbeamFt"/> abeam.
    /// </summary>
    private static LatLon PastTheBarAbeamTheStub(BarApproach approach)
    {
        double stubFt = GeoMath.DistanceNm(approach.Bar.Position, approach.RunwaySide.Position) * GeoMath.FeetPerNm;
        return Project(
            Project(approach.Bar.Position, approach.TowardRunwayDeg, stubFt - StubLeftAheadFt),
            (approach.TowardRunwayDeg + 90.0) % 360.0,
            StubAbeamFt
        );
    }

    /// <summary>Where the vacated row stands: on the stub's line, clear of the painted stop on the runway side.</summary>
    private static LatLon VacatedOnTheStub(BarApproach approach) =>
        Project(approach.Bar.Position, approach.TowardRunwayDeg, PaintedStopFt + ClearOfThePaintedStopFt);

    /// <summary>
    /// The first OAK runway bar (by runway, then node id) with a straight edge to the runway side at least <see cref="MinStubFt"/>
    /// long and a straight edge away from it, on whose stub both runway-side rows stand on that very edge
    /// (<see cref="AirportGroundLayout.FindOccupiedTaxiEdge"/>).
    /// </summary>
    private BarApproach LongStubBar(AirportGroundLayout layout)
    {
        foreach (GroundRunway runway in layout.Runways)
        {
            foreach (GroundNode bar in layout.GetRunwayHoldShortNodes(runway.Id.End1).Where(n => n.IsShortBar == false).OrderBy(n => n.Id))
            {
                if (LongStubApproach(layout, runway, bar) is { } approach)
                {
                    double stubFt = GeoMath.DistanceNm(bar.Position, approach.RunwaySide.Position) * GeoMath.FeetPerNm;
                    output.WriteLine(
                        $"runway {runway.Id} bar {bar.Id}: stub to {approach.RunwaySide.Id} is {stubFt:F0} ft, outboard {approach.Outboard.Id}"
                    );
                    return approach;
                }
            }
        }

        Assert.Fail($"no OAK runway bar has a straight runway-side stub of {MinStubFt:F0} ft or more");
        return default;
    }

    private static BarApproach? LongStubApproach(AirportGroundLayout layout, GroundRunway runway, GroundNode bar)
    {
        double barFt = CentrelineFt(runway, bar.Position);
        GroundEdge? stub = bar
            .Edges.OfType<GroundEdge>()
            .FirstOrDefault(edge =>
                (CentrelineFt(runway, edge.OtherNode(bar).Position) < barFt) && ((edge.DistanceNm * GeoMath.FeetPerNm) >= MinStubFt)
            );
        GroundEdge? away = bar.Edges.OfType<GroundEdge>().FirstOrDefault(edge => CentrelineFt(runway, edge.OtherNode(bar).Position) > barFt);
        if ((stub is null) || (away is null))
        {
            return null;
        }

        var approach = new BarApproach(bar, away.OtherNode(bar), stub.OtherNode(bar));
        bool onStub =
            ReferenceEquals(layout.FindOccupiedTaxiEdge(PastTheBarAbeamTheStub(approach)), stub)
            && ReferenceEquals(layout.FindOccupiedTaxiEdge(VacatedOnTheStub(approach)), stub);
        return onStub ? approach : null;
    }

    /// <summary>A one-segment route leaving <paramref name="from"/> for <paramref name="to"/> along the edge between them.</summary>
    private static TaxiRoute RouteAlong(GroundNode from, GroundNode to)
    {
        IGroundEdge? edge = from.Edges.FirstOrDefault(e => e.OtherNode(from) == to);
        Assert.NotNull(edge);
        return new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = edge.TaxiwayName, Edge = edge.Directed(from, to) }],
            HoldShortPoints = [],
        };
    }

    /// <summary>
    /// An aircraft on the straight stub between an OAK runway bar and its runway, its centre past the holding position marking
    /// by more than the painted stop and far enough abeam the stub's line that pure pursuit could not close onto it, with a
    /// route that starts at the bar and crosses the runway: it is already past the bar along the route's first leg, so it is
    /// not driven back to the bar.
    /// </summary>
    [Fact]
    public void PastTheBarOnItsRunwaySide_RouteAcrossTheRunway_AddsNoLeg()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        BarApproach stub = LongStubBar(layout);
        LatLon position = PastTheBarAbeamTheStub(stub);
        double pastFt = GeoMath.DistanceNm(stub.Bar.Position, position) * GeoMath.FeetPerNm;
        TaxiRoute route = RouteAlong(stub.Bar, stub.RunwaySide);

        TaxiRoute result = TaxiApproachLeg.Prepend(layout, position, new TrueHeading(stub.TowardRunwayDeg), route, BarAircraftLengthFt);

        Assert.True(ReferenceEquals(route, result), $"a leg was prepended to bar {stub.Bar.Id} from {pastFt:F0} ft past it on the runway side");
    }

    /// <summary>
    /// On the straight K edge leading to OAK's runway 33 bar, the leg to the bar is laid only from behind the bar's painted
    /// stop: 2 ft inside it the aircraft is waiting at the bar and gets none, 2 ft beyond it gets one ending at the bar.
    /// </summary>
    [Theory]
    [InlineData(-2.0, false)]
    [InlineData(2.0, true)]
    public void OnTheApproachEdge_LegOnlyFromBehindThePaintedStop(double beyondStopFt, bool expectLeg)
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        BarApproach k = K33Bar(layout);
        double behindFt = PaintedStopFt + beyondStopFt;
        LatLon position = Project(k.Bar.Position, k.AwayDeg, behindFt);
        TaxiRoute route = RouteAlong(k.Bar, k.RunwaySide);

        TaxiRoute result = TaxiApproachLeg.Prepend(layout, position, new TrueHeading((k.AwayDeg + 180.0) % 360.0), route, BarAircraftLengthFt);

        if (!expectLeg)
        {
            Assert.True(ReferenceEquals(route, result), $"a leg was prepended to bar {k.Bar.Id} from {behindFt:F1} ft behind it");
            return;
        }

        Assert.True(result.Segments[0].FromNodeId < 0, $"no leg was prepended to bar {k.Bar.Id} from {behindFt:F1} ft behind it");
        Assert.Equal(k.Bar.Id, result.Segments[0].ToNodeId);
    }

    /// <summary>
    /// Behind OAK's runway 33 bar on K but off every taxi edge: the aircraft is not on the straight edge leading to the bar,
    /// so the route keeps the bar as its start and no leg is laid.
    /// </summary>
    [Fact]
    public void BehindTheBarButOffEveryTaxiEdge_AddsNoLeg()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Debug, 100);
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).CaptureInto(tap).EnableCategory("TaxiApproachLeg", LogLevel.Debug).InitializeSimLog();
        if (new TestAirportGroundData().GetLayout("OAK") is not { } layout)
        {
            return;
        }

        BarApproach k = K33Bar(layout);
        LatLon behind = Project(k.Bar.Position, k.AwayDeg, PaintedStopFt + ClearOfThePaintedStopFt);
        LatLon? offEdge = null;
        foreach (double abeamFt in (double[])[60.0, -60.0, 90.0, -90.0])
        {
            LatLon candidate = Project(behind, (k.AwayDeg + 90.0) % 360.0, abeamFt);
            if (
                (layout.FindNearestTaxiEdge(candidate) is not { } nearest)
                || ((nearest.DistNm * GeoMath.FeetPerNm) > AirportGroundLayout.OnTaxiEdgeMaxOffsetFt)
            )
            {
                offEdge = candidate;
                break;
            }
        }

        Assert.True(
            offEdge is not null,
            $"no pose abeam K behind bar {k.Bar.Id} lies more than {AirportGroundLayout.OnTaxiEdgeMaxOffsetFt:F0} ft off every taxi edge"
        );
        LatLon position = offEdge.Value;
        TaxiRoute route = RouteAlong(k.Bar, k.RunwaySide);
        tap.Drain();

        TaxiRoute result = TaxiApproachLeg.Prepend(
            layout,
            position,
            new TrueHeading(GeoMath.BearingTo(position, k.Bar.Position)),
            route,
            BarAircraftLengthFt
        );

        Assert.True(ReferenceEquals(route, result), $"a leg was prepended to bar {k.Bar.Id} from off every taxi edge");
        Assert.Contains(tap.Drain(), record => record.Message.Contains("not on the taxi edge", StringComparison.Ordinal));
    }

    /// <summary>
    /// An aircraft that has vacated the runway and stopped on the straight stub past an OAK runway bar, more than the painted
    /// stop from it, with a route that starts at the bar and leads away from the runway: it is behind the bar along the
    /// route's first leg, so the leg drives it out through the bar, which holds nothing for traffic leaving the runway
    /// (AIM 2-3-5.a.1).
    /// </summary>
    [Fact]
    public void VacatedOntoTheStubPastTheBar_RouteAwayFromTheRunway_LegsOutThroughTheBar()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        BarApproach stub = LongStubBar(layout);
        LatLon position = VacatedOnTheStub(stub);
        TaxiRoute route = RouteAlong(stub.Bar, stub.Outboard);

        TaxiRoute result = TaxiApproachLeg.Prepend(layout, position, new TrueHeading(stub.AwayDeg), route, BarAircraftLengthFt);

        Assert.True(result.Segments[0].FromNodeId < 0, $"no leg was prepended to bar {stub.Bar.Id} from its runway side");
        Assert.Equal(stub.Bar.Id, result.Segments[0].ToNodeId);
        Assert.Null(result.GetHoldShortAt(stub.Bar.Id));
        Assert.False(
            HoldShortAnnotator.StartBarHolds(layout, stub.Bar, stub.Outboard, StartPose.Of(position, stub.AwayDeg, BarAircraftType)),
            $"bar {stub.Bar.Id} holds an aircraft leaving the runway past it"
        );
    }

    private static bool IsRunwayName(string name) => name.StartsWith("RWY", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An aircraft that has already rolled past the start node, with the route continuing ahead, must not be
    /// sent back to it — pure pursuit converges onto the line from where it is.
    ///
    /// <para>Driven through <see cref="TaxiApproachLeg.Prepend"/> directly: from this pose
    /// <see cref="AirportGroundLayout.FindTaxiStartNode"/> rejects the node behind and resolves the route
    /// from the next T node ahead instead, so the shape only reaches the leg through the plain nearest-node
    /// fallback.</para>
    /// </summary>
    [Fact]
    public void PastTheStartNodeWithTheRouteAhead_AddsNoLeg()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(StraightFromNodeId, out GroundNode? from), $"node {StraightFromNodeId} missing from the OAK layout");
        Assert.True(layout.Nodes.TryGetValue(StraightToNodeId, out GroundNode? to), $"node {StraightToNodeId} missing from the OAK layout");

        IGroundEdge? alongEdge = from.Edges.FirstOrDefault(e => e.OtherNode(from).Id == StraightToNodeId);
        Assert.NotNull(alongEdge);

        double alongBearing = GeoMath.BearingTo(from.Position, to.Position);
        LatLon position = Project(from.Position, alongBearing, PastNodeStandoffFt);
        output.WriteLine(
            $"aircraft {PastNodeStandoffFt:F0} ft past node {StraightFromNodeId} toward {StraightToNodeId} " + $"on bearing {alongBearing:F0}"
        );

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = alongEdge.TaxiwayName, Edge = alongEdge.Directed(from, to) }],
            HoldShortPoints = [],
        };

        TaxiRoute result = TaxiApproachLeg.Prepend(layout, position, new TrueHeading(alongBearing), route, AircraftLength.ResolveFt(AircraftType));

        Assert.Same(route, result);
    }

    /// <summary>
    /// An aircraft that has rolled beyond the end of the route's first segment, on its line, has driven past the
    /// start node and is not sent back to it — there is no length of that segment left to be abeam of.
    /// </summary>
    [Fact]
    public void BeyondTheFirstSegmentsEnd_AddsNoLeg()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(StraightFromNodeId, out GroundNode? from), $"node {StraightFromNodeId} missing from the OAK layout");
        Assert.True(layout.Nodes.TryGetValue(StraightToNodeId, out GroundNode? to), $"node {StraightToNodeId} missing from the OAK layout");

        IGroundEdge? alongEdge = from.Edges.FirstOrDefault(e => e.OtherNode(from).Id == StraightToNodeId);
        Assert.NotNull(alongEdge);

        double alongBearing = GeoMath.BearingTo(from.Position, to.Position);
        LatLon position = Project(to.Position, alongBearing, PastNodeStandoffFt);
        output.WriteLine($"aircraft {PastNodeStandoffFt:F0} ft beyond node {StraightToNodeId}, the end of the segment from {StraightFromNodeId}");

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = alongEdge.TaxiwayName, Edge = alongEdge.Directed(from, to) }],
            HoldShortPoints = [],
        };

        TaxiRoute result = TaxiApproachLeg.Prepend(layout, position, new TrueHeading(alongBearing), route, AircraftLength.ResolveFt(AircraftType));

        Assert.Same(route, result);
    }

    /// <summary>
    /// An aircraft past the start node but far abeam the first segment's line — more cross-track than half the length
    /// still ahead of it — cannot close onto the line inside that segment, so the leg is prepended and driven.
    /// </summary>
    [Fact]
    public void PastTheStartNodeButFarOffTheLine_PrependsTheLeg()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        Assert.True(layout.Nodes.TryGetValue(StraightFromNodeId, out GroundNode? from), $"node {StraightFromNodeId} missing from the OAK layout");
        Assert.True(layout.Nodes.TryGetValue(StraightToNodeId, out GroundNode? to), $"node {StraightToNodeId} missing from the OAK layout");
        IGroundEdge? alongEdge = from.Edges.FirstOrDefault(e => e.OtherNode(from).Id == StraightToNodeId);
        Assert.NotNull(alongEdge);

        double alongBearing = GeoMath.BearingTo(from.Position, to.Position);
        double remainingFt = (alongEdge.DistanceNm * GeoMath.FeetPerNm) - PastNodeStandoffFt;
        double abeamFt = remainingFt * 0.75;
        LatLon position = Project(Project(from.Position, alongBearing, PastNodeStandoffFt), (alongBearing + 90.0) % 360.0, abeamFt);
        output.WriteLine(
            $"aircraft {PastNodeStandoffFt:F0} ft past node {StraightFromNodeId}, {abeamFt:F0} ft abeam with {remainingFt:F0} ft of the segment left"
        );

        var route = new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = alongEdge.TaxiwayName, Edge = alongEdge.Directed(from, to) }],
            HoldShortPoints = [],
            SpotLineUpPullFromSegment = 0,
        };

        TaxiRoute result = TaxiApproachLeg.Prepend(layout, position, new TrueHeading(alongBearing), route, AircraftLength.ResolveFt(AircraftType));

        Assert.NotSame(route, result);
        Assert.True(result.Segments[0].FromNodeId < 0, "the first segment is not the free-space leg from the aircraft");
        Assert.Equal(StraightFromNodeId, result.Segments[0].ToNodeId);
        Assert.Equal(1, result.SpotLineUpPullFromSegment);
    }

    /// <summary>Runway 28R's centreline, as the layout names its edges.</summary>
    private const string Runway28REdgeName = "RWY28R/10L";

    /// <summary>The fillet arcs joining taxiway G and runway 28R.</summary>
    private const string G28RFilletName = "G - RWY28R/10L";

    /// <summary>
    /// OAK taxiway G's 28R exit fillet: <c>Exit</c>, its from-node on the runway centerline (of the two G fillet ends on 28R,
    /// the one a 28R rollout reaches first), and <c>North</c>, its far end off the runway on taxiway G.
    /// </summary>
    private static (GroundNode Exit, GroundNode North) G28RExitFillet(AirportGroundLayout layout)
    {
        List<GroundNode> runwayEnds =
        [
            .. layout.Nodes.Values.Where(node => HasEdgeNamed(node, Runway28REdgeName) && HasEdgeNamed(node, G28RFilletName)),
        ];
        Assert.NotEmpty(runwayEnds);
        GroundNode exit = runwayEnds.MinBy(node => AlongRunwayFt(runwayEnds[0], node))!;
        GroundNode? north = exit
            .Edges.Where(edge => edge.TaxiwayName == G28RFilletName)
            .Select(edge => edge.OtherNode(exit))
            .MaxBy(node => node.Position.Lat);
        Assert.NotNull(north);
        return (exit, north);
    }

    /// <summary>How far (ft) <paramref name="to"/> lies from <paramref name="from"/> along 28R's landing direction.</summary>
    private static double AlongRunwayFt(GroundNode from, GroundNode to) =>
        GeoMath.DistanceNm(from.Position, to.Position)
        * GeoMath.FeetPerNm
        * Math.Cos((GeoMath.BearingTo(from.Position, to.Position) - Runway28RHeadingDeg) * Math.PI / 180.0);

    /// <summary>The exit fillet's departure bearing: runway 28R's landing direction at that node.</summary>
    private const double Runway28RHeadingDeg = 292.0;

    /// <summary>How far back along the runway the rolling-out aircraft still is — a rollout is far from its exit.</summary>
    private const double RolloutBehindExitFt = 300.0;

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
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        GroundNode exit = G28RExitFillet(layout).Exit;

        LatLon position = Project(exit.Position, (Runway28RHeadingDeg + 180.0) % 360.0, RolloutBehindExitFt);
        output.WriteLine($"aircraft {RolloutBehindExitFt:F0} ft back along 28R from node {exit.Id}, facing {Runway28RHeadingDeg:F0}");

        AircraftState aircraft = MakeAircraft(layout, position, Runway28RHeadingDeg);
        TaxiRoute route = Resolve(layout, aircraft, new TaxiCommand(Path: ["G", "D", "J"], HoldShorts: [], Expedite: false, DestinationRunway: null));

        TaxiRouteSegment leg = route.Segments[0];
        double legStartOffsetFt = GeoMath.DistanceNm(leg.Edge.FromNode.Position, aircraft.Position) * GeoMath.FeetPerNm;
        output.WriteLine($"leg starts {legStartOffsetFt:F2} ft from the aircraft, {leg.Edge.DistanceNm * GeoMath.FeetPerNm:F0} ft long");

        Assert.True(leg.FromNodeId < 0, $"first segment starts at layout node {leg.FromNodeId}, not at a virtual node on the aircraft");
        Assert.Equal(exit.Id, leg.ToNodeId);
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
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        (GroundNode exit, GroundNode node) = G28RExitFillet(layout);
        Assert.NotEqual(GroundNodeType.RunwayHoldShort, node.Type);

        IGroundEdge? onward = node
            .Edges.Where(e => e is not GroundArc)
            .OrderByDescending(e => GeoMath.DistanceNm(node.Position, e.OtherNode(node).Position))
            .FirstOrDefault();
        Assert.NotNull(onward);

        GroundNode onwardNode = onward.OtherNode(node);

        // Back along 28R, then out to its south side: the line from there to the node north of the runway
        // crosses the centerline well away from any node, and the drive stays inside the apron-length bound.
        LatLon alongRunway = Project(exit.Position, (Runway28RHeadingDeg + 180.0) % 360.0, AcrossRunwayAlongFt);
        LatLon position = Project(alongRunway, (Runway28RHeadingDeg + 270.0) % 360.0, AcrossRunwayOffsetFt);
        double distFt = GeoMath.DistanceNm(position, node.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"aircraft {distFt:F0} ft from node {node.Id} (the north end of G's 28R exit fillet from node {exit.Id}), on the far side of 28R; "
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

        Assert.Same(
            route,
            TaxiApproachLeg.Prepend(layout, position, new TrueHeading(Runway28RHeadingDeg), route, AircraftLength.ResolveFt(AircraftType))
        );
    }

    /// <summary>
    /// A real node on OAK taxiway B, where the A/B fillet leaves it: 540 ft south of the 28R centerline and about 2,460 ft back
    /// along the runway from G's 28R exit fillet. The arc is matched on its taxiway names as a set, since its display name's
    /// order (<c>A - B</c> or <c>B - A</c>) follows the parse. A meets B with a fillet on each side, so of the two B ends the
    /// one farther from <paramref name="exit"/> is taken.
    /// </summary>
    private static GroundNode ParallelTaxiwayNode(AirportGroundLayout layout, GroundNode exit)
    {
        List<GroundNode> filletEnds =
        [
            .. layout.Nodes.Values.Where(node =>
                node.Edges.Any(e => (e is GroundEdge) && e.MatchesTaxiway("B"))
                && node.Edges.OfType<GroundArc>().Any(a => a.TaxiwayNames.Contains("A") && a.TaxiwayNames.Contains("B"))
            ),
        ];
        Assert.True(filletEnds.Count == 2, $"expected the A/B fillets' two ends on B, found {filletEnds.Count}");
        return filletEnds.MaxBy(node => GeoMath.DistanceNm(node.Position, exit.Position))!;
    }

    /// <summary>A heading across 28R rather than along it: an aircraft on the pavement pointed at the far side.</summary>
    private const double AcrossRunwayHeadingDeg = 20.0;

    /// <summary>The along-runway bearing tolerance the fixtures are built against.</summary>
    private const double AlongRunwayToleranceDeg = 15.0;

    /// <summary>
    /// The one-segment route a rollout resolves to: OAK taxiway G's 28R exit fillet, leaving its runway end for the
    /// taxiway side. Built directly rather than through <c>TryTaxi</c> so only the approach leg's own guards
    /// decide — a route resolved for an aircraft standing somewhere else starts at whatever node is nearest IT.
    /// </summary>
    private static TaxiRoute ExitFilletRoute(AirportGroundLayout layout)
    {
        (GroundNode exit, GroundNode north) = G28RExitFillet(layout);
        IGroundEdge? fillet = exit.Edges.FirstOrDefault(e => e.OtherNode(exit) == north);
        Assert.NotNull(fillet);

        return new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = fillet.TaxiwayName, Edge = fillet.Directed(exit, fillet.OtherNode(exit)) }],
            HoldShortPoints = [],
        };
    }

    /// <summary>
    /// A parallel taxiway is not the runway. Bearing alone admitted an aircraft holding on OAK taxiway B — 540 ft
    /// south of the 28R centerline — because its line to G's 28R exit fillet, 2,460 ft up the field, still ran within 15° of
    /// the runway, and the along-runway exception then skipped both the length bound and the runway-crossing
    /// refusal. The leg it was given ran across B's holding position marking and onto the runway
    /// (AIM 4-3-18.a.5, AIM 2-3-5.a.1), so the exception must test that the aircraft is ON that runway.
    /// </summary>
    [Fact]
    public void OnAParallelTaxiwayAbeamTheRunway_AddsNoLeg()
    {
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        GroundNode exit = G28RExitFillet(layout).Exit;
        GroundNode abeam = ParallelTaxiwayNode(layout, exit);

        double bearingToExit = GeoMath.BearingTo(abeam.Position, exit.Position);
        double offRunwayDeg = GeoMath.AbsBearingDifference(bearingToExit, Runway28RHeadingDeg);
        double distFt = GeoMath.DistanceNm(abeam.Position, exit.Position) * GeoMath.FeetPerNm;
        double abeamFt =
            Math.Abs(GeoMath.SignedCrossTrackDistanceNm(abeam.Position, exit.Position, new TrueHeading(Runway28RHeadingDeg))) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"node {abeam.Id} sits {abeamFt:F0} ft abeam the 28R centerline, {distFt:F0} ft from node {exit.Id} "
                + $"on bearing {bearingToExit:F1} ({offRunwayDeg:F1}° off the runway)"
        );

        Assert.True(offRunwayDeg <= AlongRunwayToleranceDeg, $"the fixture must sit within {AlongRunwayToleranceDeg:F0}° of the runway bearing");
        Assert.True(abeamFt > 100.0, $"the fixture must sit off the runway pavement, but it is {abeamFt:F0} ft from the centerline");

        TaxiRoute route = ExitFilletRoute(layout);

        Assert.Same(
            route,
            TaxiApproachLeg.Prepend(layout, abeam.Position, new TrueHeading(Runway28RHeadingDeg), route, AircraftLength.ResolveFt(AircraftType))
        );
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
        AirportGroundLayout? layout = LoadOakLayout();
        if (layout is null)
        {
            return;
        }

        GroundNode exit = G28RExitFillet(layout).Exit;

        LatLon position = Project(exit.Position, (Runway28RHeadingDeg + 180.0) % 360.0, RolloutBehindExitFt);
        double bearingToExit = GeoMath.BearingTo(position, exit.Position);
        output.WriteLine(
            $"aircraft {RolloutBehindExitFt:F0} ft back along 28R from node {exit.Id} (bearing {bearingToExit:F1}), "
                + $"facing {AcrossRunwayHeadingDeg:F0} — across the runway, not along it"
        );

        TaxiRoute route = ExitFilletRoute(layout);

        Assert.Same(
            route,
            TaxiApproachLeg.Prepend(layout, position, new TrueHeading(AcrossRunwayHeadingDeg), route, AircraftLength.ResolveFt(AircraftType))
        );
    }

    /// <summary>SFO node 120, where B bends between its edges to nodes 119 and 12 (the THY9WC push's lane).</summary>
    private const int SfoBBendNodeId = 120;

    /// <summary>The B node the aircraft is nosed toward from <see cref="SfoBBendNodeId"/>.</summary>
    private const int SfoBSouthWestNodeId = 119;

    /// <summary>
    /// An aircraft standing at B's node 120, nosed along B's edge toward node 119: the lane is named through the node, and the
    /// lane edge it is measured against is the B edge at that node most nearly along the nose, 120–119. The nose lies along
    /// it, so the FACE alternative is the reverse of the nose, not the route's first-leg bearing (passed here as the nose
    /// itself, so the two cannot coincide).
    /// </summary>
    [Fact]
    public void AtABNode_NosedAlongB_LaneEdgeIsTheBEdgeAlongTheNoseAndFaceIsItsReverse()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        GroundNode bend = ground.Layout.Nodes[SfoBBendNodeId];
        GroundNode southWest = ground.Layout.Nodes[SfoBSouthWestNodeId];
        double alongB = GeoMath.BearingTo(bend.Position, southWest.Position);
        AircraftState aircraft = MakeAircraft(ground.Layout, bend.Position, alongB);

        GroundCommandHandler.LaneStand stand = Assert.IsType<GroundCommandHandler.LaneStand>(
            GroundCommandHandler.StandsOnTaxiway(ground.Layout, aircraft, "B")
        );
        GroundEdge laneEdge = Assert.IsType<GroundEdge>(stand.Edge);
        output.WriteLine($"lane edge {laneEdge.Nodes[0].Id}-{laneEdge.Nodes[1].Id}; nose true {alongB:F1}");

        Assert.True(laneEdge.Nodes.Contains(bend) && laneEdge.Nodes.Contains(southWest), "the lane edge is not B 120-119");
        Assert.Equal(aircraft.MagneticHeading.ToReciprocal(), GroundCommandHandler.RouteFacing(aircraft, stand.Edge, alongB));
    }
}
