using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A TAXI given to an aircraft on a taxi fillet arc that ends at an uncleared runway bar's node, well short of the bar, gets an
/// approach leg to the bar like an aircraft on a straight approach edge (<see cref="TaxiFromShortOfARunwayBarTests"/>), so it
/// stops with no part past the holding position marking (AIM 2-3-5.a.1) rather than its centre braking to the bar's node. The
/// arc is followed only where it is the aircraft's own way to the bar: from inside the bar's painted stop there is nothing to
/// drive, and an arc whose end is one edge short of the bar, or whose aircraft is already on the bar's runway side, is left
/// alone.
/// </summary>
public class TaxiFromAFilletArcToARunwayBarTests(ITestOutputHelper output)
{
    private const string Callsign = "N738SP";

    private const string AircraftType = "B738";

    /// <summary>How far (ft) past the marking a part may come to rest: the braking curve's own slack, as on an approach edge.</summary>
    private const double PastMarkingToleranceFt = 1.0;

    private const double StopToleranceFt = 1.0;

    private const int RestBudgetSeconds = 120;

    /// <summary>
    /// How far along the arc from the bar node the aircraft stands, ft: the farthest out it is on the arc and on no straight
    /// edge, past the 150 ft start-node hold radius (<c>TaxiingPhase.StartNodeHoldRadiusFt</c>) by about a foot in a straight line.
    /// </summary>
    private const double StartAlongArcFt = 152.0;

    /// <summary>How far (ft) inside the bar's painted stop an aircraft already waiting at it stands.</summary>
    private const double InsideThePaintedStopFt = 20.0;

    /// <summary>How far (ft) in from the arc's far end an aircraft on it stands, clear of the node it is heading for.</summary>
    private const double ArcEndClearanceFt = 40.0;

    /// <summary>The ATL bar the census lists with a taxi fillet arc on the bar's own runway side.</summary>
    private const int RunwaySideBarId = 768;

    private const string StartingConditionsToken = "\"STARTING_CONDITIONS\"";

    private const string Scenario = """
        {
          "id": "01JH02G8F0L1W5T7R9N2K4X6QD",
          "name": "TAXI from a fillet arc ending at an uncleared runway bar",
          "primaryAirportId": "ATL",
          "aircraft": [
            {
              "id": "01JH02N738SPG8F0L1W5T7R9NE",
              "aircraftId": "N738SP",
              "aircraftType": "B738",
              "startingConditions": "STARTING_CONDITIONS",
              "flightplan": { "rules": "IFR", "departure": "KATL", "destination": "KCLT", "aircraftType": "B738" },
              "airportId": "ATL"
            }
          ]
        }
        """;

    /// <summary>
    /// KATL's longest taxi fillet arc ending at a runway bar from the side away from the runway (M - K, about 200 ft, at a
    /// runway 9L/27R bar on K), the one bar of the committed layouts reached from more than 150 ft out on an arc alone. A TAXI K
    /// from 152 ft along that arc starts the route on the bar's node; the aircraft must get an approach leg to the bar and stop
    /// with no part of it past the marking, its centre half a fuselage short of the bar.
    /// </summary>
    [Fact]
    public void TaxiFromFarAlongAFilletArcEndingAtAnUnclearedRunwayBar_StopsEveryPartAtTheMarking()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? maybeLayout = groundData.GetLayout("ATL");
        Assert.NotNull(maybeLayout);
        AirportGroundLayout layout = maybeLayout;

        (GroundNode bar, GroundArc arc) = LongestApproachArcEndingAtARunwayBar(layout);
        RunwayInfo runway = RunwayOf(layout, bar);
        double arcFt = arc.DistanceNm * GeoMath.FeetPerNm;
        LatLon start = arc.PointAtFt(bar, StartAlongArcFt);
        double startFromBarFt = GeoMath.DistanceNm(start, bar.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"bar #{bar.Id} ({bar.RunwayId}), arc {arc.TaxiwayName} {arcFt:F0} ft from #{arc.OtherNode(bar).Id}, start {startFromBarFt:F0} ft"
        );
        Assert.True(startFromBarFt > 150.0, $"the start on arc {arc.TaxiwayName} is only {startFromBarFt:F0} ft from bar #{bar.Id}");
        Assert.True(
            layout.FindOccupiedTaxiEdge(start) is null,
            $"the start {StartAlongArcFt:F0} ft along arc {arc.TaxiwayName} from bar #{bar.Id} lies on a straight taxi edge, not on the arc"
        );
        Assert.Same(arc, layout.FindOccupiedFilletArc(start));

        double towardDeg = GeoMath.BearingTo(start, arc.PointAtFt(bar, StartAlongArcFt - 1.0));
        double headingMag = MagneticDeclination.TrueToMagnetic(towardDeg, start, MagneticDeclination.EvaluationDateUtc);
        string startingConditions =
            $$"""{ "type": "Coordinates", "coordinates": { "lat": {{start.Lat:R}}, "lon": {{start.Lon:R}} }, "heading": {{headingMag:R}} }""";
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.LoadScenario(
            Scenario.Replace(StartingConditionsToken, startingConditions, StringComparison.Ordinal),
            1,
            MagneticDeclination.EvaluationDateUtc
        );
        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        double halfLengthFt = AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0;

        // A coordinates spawn snaps onto the nearest straight edge (M here), so the aircraft is set on the arc once spawned.
        aircraft.Position = start;
        aircraft.TrueHeading = new TrueHeading(towardDeg);

        const string taxi = "TAXI K";
        AssertAccepted(engine.SendCommand(Callsign, taxi), taxi);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.ToSummary()}, first segment {route.Segments[0].FromNodeId} -> {route.Segments[0].ToNodeId}");
        Assert.True(
            (route.Segments[0].FromNodeId < 0) && (route.Segments[0].ToNodeId == bar.Id),
            $"{taxi} from {StartAlongArcFt:F0} ft along arc {arc.TaxiwayName} laid no approach leg to bar #{bar.Id}: the route starts "
                + $"{route.Segments[0].FromNodeId} -> {route.Segments[0].ToNodeId}"
        );
        HoldShortPoint hold = Assert.IsType<HoldShortPoint>(route.GetHoldShortAt(bar.Id));
        Assert.False(hold.IsCleared, $"{taxi} cleared runway {bar.RunwayId} at node {bar.Id}");
        var stop = new LatLon(Assert.IsType<double>(hold.Latitude), Assert.IsType<double>(hold.Longitude));
        double stopShortOfBarFt = GeoMath.DistanceNm(stop, bar.Position) * GeoMath.FeetPerNm;
        Assert.True(
            Math.Abs(stopShortOfBarFt - halfLengthFt) <= StopToleranceFt,
            $"{taxi} puts its stop {stopShortOfBarFt:F1} ft from bar node {bar.Id}, not half the fuselage ({halfLengthFt:F1} ft)"
        );

        for (int second = 0; (second < RestBudgetSeconds) && !((second > 5) && (aircraft.GroundSpeed < 0.01)); second++)
        {
            engine.TickOneSecond();
        }

        string headingReport =
            $"heading at rest {aircraft.TrueHeading.Degrees:F1}° true against K {RunwayHoldBars.TaxiwayAxisBearingDeg(runway, bar):F1}° true";
        List<(AircraftBodyPart Part, double DepthFt)> depths =
        [
            .. StartPose
                .Of(aircraft)
                .PartPositions()
                .Select(
                    (part, index) => (AircraftBody.AllParts[index], RunwayHoldBars.DepthPastMarkingFt(layout, runway, bar, part, aircraft.Position))
                ),
        ];
        (AircraftBodyPart deepestPart, double deepestPastFt) = depths.MaxBy(entry => entry.DepthFt);
        Assert.True(
            aircraft.GroundSpeed < 0.01,
            $"the B738 is still rolling at {aircraft.GroundSpeed:F2} kt after the {RestBudgetSeconds} s budget, its {deepestPart} "
                + $"{deepestPastFt:F1} ft past runway {bar.RunwayId}'s holding position marking ({headingReport})"
        );
        Assert.True(
            deepestPastFt <= PastMarkingToleranceFt,
            $"the B738's {deepestPart} came to rest {deepestPastFt:F1} ft past runway {bar.RunwayId}'s holding position marking "
                + $"({headingReport}, gs {aircraft.GroundSpeed:F2}, phase {aircraft.Phases?.CurrentPhase?.Name})"
        );
        double nosePastFt = depths[0].DepthFt;
        Assert.True(
            nosePastFt >= -(StopToleranceFt + GroundNavigator.SetBackStopMarginFt + 1.0),
            $"the B738 came to rest with its nose {-nosePastFt:F1} ft short of runway {bar.RunwayId}'s holding position marking, not at it "
                + $"({headingReport})"
        );
    }

    /// <summary>
    /// An aircraft on the same M - K arc but inside the bar's painted stop is already waiting at the bar: no leg is prepended
    /// and the route stays the one the taxi start picked, as before the arc was followed at all.
    /// </summary>
    [Fact]
    public void OnTheSameArcInsideThePaintedStop_NoLegIsBuilt()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = LoadAtlLayout();
        (GroundNode bar, GroundArc arc) = LongestApproachArcEndingAtARunwayBar(layout);
        double paintedStopFt = (AircraftLength.ResolveFt(AircraftType) / 2.0) + GroundNavigator.SetBackStopMarginFt;
        double alongFt = paintedStopFt - InsideThePaintedStopFt;
        LatLon position = arc.PointAtFt(bar, alongFt);
        Assert.Same(arc, layout.FindOccupiedFilletArc(position));
        TaxiRoute route = RouteAwayFromTheRunway(layout, bar);

        TaxiRoute result = TaxiApproachLeg.Prepend(
            layout,
            position,
            new TrueHeading(GeoMath.BearingTo(position, bar.Position)),
            route,
            AircraftLength.ResolveFt(AircraftType)
        );

        output.WriteLine($"{alongFt:F1} ft along arc {arc.TaxiwayName} from bar #{bar.Id}, inside its {paintedStopFt:F1} ft painted stop");
        Assert.True(
            ReferenceEquals(route, result),
            $"{alongFt:F1} ft from bar #{bar.Id} is inside its {paintedStopFt:F1} ft painted stop, so no leg may be prepended: the route "
                + $"starts {result.Segments[0].FromNodeId} -> {result.Segments[0].ToNodeId}"
        );
    }

    /// <summary>
    /// An arc that ends one straight edge short of a bar is not followed: the aircraft on it gets the ordinary free-space
    /// treatment to the arc's own end node, never a leg to the bar's node.
    /// </summary>
    [Fact]
    public void AnArcEndingOneEdgeShortOfTheBar_AddsNoLegToTheBar()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = LoadAtlLayout();
        (GroundNode bar, GroundArc arc, GroundNode arcEnd, LatLon position) = ArcOneHopShortOfABar(layout);
        TaxiRoute route = RouteAlong(arcEnd, bar);
        output.WriteLine(
            $"bar #{bar.Id} is one {arcEnd.Id}-{bar.Id} edge from arc {arc.TaxiwayName} {arc.DistanceNm * GeoMath.FeetPerNm:F0} ft; "
                + $"pose {ArcEndClearanceFt:F0} ft in from node {arcEnd.Id}"
        );

        TaxiRoute result = TaxiApproachLeg.Prepend(
            layout,
            position,
            new TrueHeading(GeoMath.BearingTo(position, arcEnd.Position)),
            route,
            AircraftLength.ResolveFt(AircraftType)
        );

        Assert.True(
            (result.Segments[0].FromNodeId >= 0) || (result.Segments[0].ToNodeId == arcEnd.Id),
            $"the arc's end node {arcEnd.Id} is one edge short of bar #{bar.Id}, so no leg may end at the bar: the route starts "
                + $"{result.Segments[0].FromNodeId} -> {result.Segments[0].ToNodeId}"
        );
    }

    /// <summary>
    /// Bar #768's A - D fillet lies on the bar's own runway side, so the aircraft standing on it has already passed the bar: a
    /// route leading away from the runway must not drive it to the bar.
    /// </summary>
    [Fact]
    public void OnARunwaySideArc_RouteLeadingAwayFromTheRunway_IsNotDrivenToTheBar()
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout = LoadAtlLayout();
        (GroundNode bar, GroundArc arc, double alongFt) = RunwaySideArcPose(layout);
        LatLon position = arc.PointAtFt(bar, alongFt);
        TaxiRoute route = RouteAwayFromTheRunway(layout, bar);
        double alongRouteFt =
            GeoMath.AlongTrackDistanceNm(position, bar.Position, new TrueHeading(route.Segments[0].Edge.DepartureBearing)) * GeoMath.FeetPerNm;
        double behindFt = TaxiApproachLeg.BehindBarAlongEdgeFt(position, bar, arc);
        output.WriteLine(
            $"bar #{bar.Id} ({bar.RunwayId}), arc {arc.TaxiwayName} {arc.DistanceNm * GeoMath.FeetPerNm:F0} ft, pose {alongFt:F0} ft along it: "
                + $"{alongRouteFt:F0} ft along the route's departure, {behindFt:F0} ft behind the bar along the arc"
        );

        TaxiRoute result = TaxiApproachLeg.Prepend(
            layout,
            position,
            new TrueHeading(GeoMath.BearingTo(position, bar.Position)),
            route,
            AircraftLength.ResolveFt(AircraftType)
        );

        Assert.True(
            result.Segments[0].ToNodeId != bar.Id,
            $"a leg to bar #{bar.Id} was prepended to an aircraft already on its runway side ({alongRouteFt:F0} ft along the route's "
                + $"departure, {behindFt:F0} ft behind the bar along the arc): the route starts "
                + $"{result.Segments[0].FromNodeId} -> {result.Segments[0].ToNodeId}"
        );
    }

    private static void AssertAccepted(CommandResult result, string taxi) => Assert.True(result.Success, $"{taxi} was rejected: {result.Message}");

    private static AirportGroundLayout LoadAtlLayout()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("ATL");
        Assert.NotNull(layout);
        return layout;
    }

    /// <summary>The nav database's record of the runway <paramref name="bar"/> protects.</summary>
    private static RunwayInfo RunwayOf(AirportGroundLayout layout, GroundNode bar) =>
        Assert.Single(RunwayOccupancy.AirportRunways(layout.AirportId), runway => runway.Id.Equals(bar.RunwayId));

    /// <summary>A one-segment route leaving <paramref name="from"/> for <paramref name="to"/> along the edge between them.</summary>
    private static TaxiRoute RouteAlong(GroundNode from, GroundNode to)
    {
        IGroundEdge? edge = from.Edges.FirstOrDefault(candidate => candidate.OtherNode(from) == to);
        Assert.NotNull(edge);
        return new TaxiRoute
        {
            Segments = [new TaxiRouteSegment { TaxiwayName = edge.TaxiwayName, Edge = edge.Directed(from, to) }],
            HoldShortPoints = [],
        };
    }

    /// <summary>
    /// A one-segment route from <paramref name="bar"/> along the straight edge of its own taxiway that leads farthest away from
    /// its runway: the route a TAXI from the bar picks up, heading off the field.
    /// </summary>
    private static TaxiRoute RouteAwayFromTheRunway(AirportGroundLayout layout, GroundNode bar)
    {
        RunwayInfo runway = RunwayOf(layout, bar);
        List<GroundEdge> edges = RunwayHoldBars.BarTaxiwayEdges(bar);
        Assert.True(edges.Count > 0, $"bar #{bar.Id} carries no named straight edge off a runway centreline");
        GroundEdge away = edges.MaxBy(edge => RunwayHoldBars.CentrelineFt(runway, edge.OtherNode(bar).Position))!;
        return RouteAlong(bar, away.OtherNode(bar));
    }

    /// <summary>
    /// The longest taxi fillet arc (no ramp connector, no runway centreline) that ends at a runway bar's node from the side away
    /// from the runway (its other end farther from the runway centreline than the bar), with that bar.
    /// </summary>
    private static (GroundNode Bar, GroundArc Arc) LongestApproachArcEndingAtARunwayBar(AirportGroundLayout layout)
    {
        List<(GroundNode Bar, GroundArc Arc)> pairs =
        [
            .. layout
                .Nodes.Values.Where(node => (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is not null))
                .SelectMany(node =>
                    node.Edges.OfType<GroundArc>()
                        .Where(arc => !arc.IsRamp && !arc.IsRunwayCenterline && LeadsAwayFromTheRunway(layout, node, arc.OtherNode(node)))
                        .Select(arc => (Bar: node, Arc: arc))
                ),
        ];
        Assert.True(pairs.Count > 0, "no runway bar is reached by a taxi fillet arc from the side away from its runway");
        return pairs.MaxBy(pair => pair.Arc.DistanceNm);
    }

    /// <summary>
    /// The first ATL runway bar reached one straight edge short of a taxi fillet arc, with that arc, its end node and a pose
    /// <see cref="ArcEndClearanceFt"/> in from that node on the arc and on no straight edge.
    /// </summary>
    private static (GroundNode Bar, GroundArc Arc, GroundNode ArcEnd, LatLon Position) ArcOneHopShortOfABar(AirportGroundLayout layout)
    {
        List<string> measured = [];
        foreach (GroundNode bar in layout.Nodes.Values.Where(IsRunwayBar).OrderBy(node => node.Id))
        {
            foreach (GroundEdge hop in bar.Edges.OfType<GroundEdge>().Where(edge => !edge.IsRunwayCenterline))
            {
                GroundNode arcEnd = hop.OtherNode(bar);
                foreach (GroundArc arc in arcEnd.Edges.OfType<GroundArc>().Where(candidate => !candidate.IsRamp && !candidate.IsRunwayCenterline))
                {
                    double arcFt = arc.DistanceNm * GeoMath.FeetPerNm;
                    if (arcFt <= (ArcEndClearanceFt + 5.0))
                    {
                        measured.Add($"arc {arc.TaxiwayName} at node {arcEnd.Id} is only {arcFt:F0} ft");
                        continue;
                    }

                    LatLon position = arc.PointAtFt(arcEnd, ArcEndClearanceFt);
                    if ((layout.FindOccupiedTaxiEdge(position) is null) && ReferenceEquals(layout.FindOccupiedFilletArc(position), arc))
                    {
                        return (bar, arc, arcEnd, position);
                    }

                    measured.Add($"no pose {ArcEndClearanceFt:F0} ft in from node {arcEnd.Id} on arc {arc.TaxiwayName} alone");
                }
            }
        }

        Assert.Fail($"no ATL runway bar is reached one straight edge short of an arc an aircraft can stand on: {string.Join("; ", measured)}");
        return default;
    }

    /// <summary>
    /// Bar <see cref="RunwaySideBarId"/>'s A - D fillet arc, with the farthest pose on it (from the bar) that is on the arc and on
    /// no straight edge.
    /// </summary>
    private static (GroundNode Bar, GroundArc Arc, double AlongFt) RunwaySideArcPose(AirportGroundLayout layout)
    {
        Assert.True(layout.Nodes.TryGetValue(RunwaySideBarId, out GroundNode? maybeBar), $"the ATL layout has no node #{RunwaySideBarId}");
        GroundNode bar = Assert.IsType<GroundNode>(maybeBar);
        Assert.True(IsRunwayBar(bar), $"node #{RunwaySideBarId} is not a runway bar");

        List<string> measured = [];
        foreach (GroundArc arc in bar.Edges.OfType<GroundArc>().Where(candidate => candidate.MatchesTaxiway("A") && candidate.MatchesTaxiway("D")))
        {
            if (FarthestOnTheArcAloneFt(layout, bar, arc) is { } alongFt)
            {
                return (bar, arc, alongFt);
            }

            measured.Add($"arc {arc.TaxiwayName} {arc.DistanceNm * GeoMath.FeetPerNm:F0} ft has no pose on the arc alone");
        }

        Assert.Fail($"bar #{RunwaySideBarId} has no A - D arc an aircraft can stand on alone: {string.Join("; ", measured)}");
        return default;
    }

    /// <summary>
    /// The farthest distance (ft) along <paramref name="arc"/> from <paramref name="from"/> at which a position is on the arc and
    /// on no straight edge; null when every foot of it is on an edge or off the arc.
    /// </summary>
    private static double? FarthestOnTheArcAloneFt(AirportGroundLayout layout, GroundNode from, GroundArc arc)
    {
        for (double alongFt = Math.Floor(arc.DistanceNm * GeoMath.FeetPerNm) - 1.0; alongFt >= 1.0; alongFt -= 1.0)
        {
            LatLon point = arc.PointAtFt(from, alongFt);
            if ((layout.FindOccupiedTaxiEdge(point) is null) && ReferenceEquals(layout.FindOccupiedFilletArc(point), arc))
            {
                return alongFt;
            }
        }

        return null;
    }

    private static bool IsRunwayBar(GroundNode node) => (node.Type == GroundNodeType.RunwayHoldShort) && (node.RunwayId is not null);

    private static bool LeadsAwayFromTheRunway(AirportGroundLayout layout, GroundNode bar, GroundNode other)
    {
        GroundRunway runway = layout.Runways.Single(r => r.Id.Equals(bar.RunwayId));
        var axisStart = new LatLon(runway.Coordinates[0].Lat, runway.Coordinates[0].Lon);
        var axisEnd = new LatLon(runway.Coordinates[^1].Lat, runway.Coordinates[^1].Lon);
        var heading = new TrueHeading(GeoMath.BearingTo(axisStart, axisEnd));
        double CrossFt(LatLon point) => Math.Abs(GeoMath.SignedCrossTrackDistanceNm(point, axisStart, heading)) * GeoMath.FeetPerNm;
        return CrossFt(other.Position) > CrossFt(bar.Position);
    }
}
