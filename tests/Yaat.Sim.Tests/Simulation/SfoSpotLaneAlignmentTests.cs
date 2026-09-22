using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A <c>$spot</c> destination is reached along the lane the spot sits on, so the aircraft comes to rest facing
/// along that lane (either way down it). The ramp cuts used to be allowed to land on the spot node itself from
/// the side, which left SFO arrivals stopped across their own lane — <c>TAXI $5A</c> from gate D2 finished on
/// spot 5A heading 45° on a lane that runs 118 / 298, and <c>TAXI $7B</c> from spot 7A finished heading 118°
/// on a lane that runs 27 / 207.
/// </summary>
public class SfoSpotLaneAlignmentTests(ITestOutputHelper output)
{
    /// <summary>Recorded session the live-dispatch case replays from: S1-SFO-2 Ground Control 28/01.</summary>
    private const string RecordingPath = "TestData/sfo-gc-28-01-spot-lanes-recording.zip";

    /// <summary>How far the resting heading may sit off the lane's bearing (or its reciprocal).</summary>
    private const double AlignmentToleranceDeg = 15.0;

    /// <summary>Ground speed below which the aircraft counts as stopped.</summary>
    private const double AtRestKts = 1.0;

    /// <summary>
    /// The flat run floor the one-fuselage rule replaced, and the lower edge of the window the two disagree
    /// over. Held here rather than read from <c>RampLaneReposition</c>, where it is now private: this is the
    /// old rule the test contrasts against, and it must not follow the production constant if that moves.
    /// </summary>
    private const double RunFloorFt = 100.0;

    /// <summary>How many spot-lane approaches the search below pathfinds before giving up.</summary>
    private const int MaxLaneCandidates = 50;

    private static double DistanceFt(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>Every direction the spot's own lane runs in at the spot node, both ways down each edge.</summary>
    private static List<double> LaneBearings(GroundNode spot, string lane)
    {
        var bearings = new List<double>();
        foreach (IGroundEdge edge in spot.Edges)
        {
            if (!edge.MatchesTaxiway(lane))
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(spot.Position, edge.OtherNode(spot).Position);
            bearings.Add(bearing);
            bearings.Add((bearing + 180.0) % 360.0);
        }

        return bearings;
    }

    /// <summary>
    /// Asserts the aircraft came to rest on the spot, facing along the spot's lane.
    /// </summary>
    /// <param name="aircraft">Aircraft under test.</param>
    /// <param name="spot">The spot it was cleared to.</param>
    /// <param name="lane">The lane the spot sits on.</param>
    private void AssertRestingOnSpotAlongLane(AircraftState aircraft, GroundNode spot, string lane)
    {
        List<double> bearings = LaneBearings(spot, lane);
        Assert.True(bearings.Count > 0, $"spot {spot.Name} has no edge of lane {lane}");

        double headingDeg = aircraft.TrueHeading.Degrees;
        double offBy = bearings.Min(b => GeoMath.AbsBearingDifference(headingDeg, b));
        double distFt = DistanceFt(aircraft.Position, spot.Position);
        double halfLengthFt = (FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? 100.0) / 2.0;
        output.WriteLine(
            $"{aircraft.Callsign}: heading {headingDeg:F1}, lane {lane} runs [{string.Join(", ", bearings.Select(b => $"{b:F0}"))}], "
                + $"off by {offBy:F1}°, {distFt:F0} ft from spot {spot.Name} (half fuselage {halfLengthFt:F0} ft), "
                + $"phase {aircraft.Phases?.CurrentPhase?.GetType().Name}, {aircraft.GroundSpeed:F1} kts"
        );

        Assert.True(aircraft.GroundSpeed < AtRestKts, $"{aircraft.Callsign} is still moving at {aircraft.GroundSpeed:F1} kts");
        Assert.True(distFt <= halfLengthFt + 5.0, $"{aircraft.Callsign} stopped {distFt:F0} ft from spot {spot.Name}");
        Assert.True(
            offBy <= AlignmentToleranceDeg,
            $"{aircraft.Callsign} rests heading {headingDeg:F1}, {offBy:F1}° off lane {lane} — expected within {AlignmentToleranceDeg:F0}°"
        );
    }

    /// <summary>The free-space cut that lands on the spot itself from the side is the shape under test.</summary>
    private static void AssertNoFreeSpaceLegIntoSpot(TaxiRoute route, GroundNode spot) =>
        Assert.DoesNotContain(route.Segments, s => (s.ToNodeId == spot.Id) && s.TaxiwayName.Equals("RAMP", StringComparison.OrdinalIgnoreCase));

    private int TickToRest(SimulationEngine engine, AircraftState aircraft, int maxSeconds) =>
        SfoGroundHarness.TickUntil(
            engine,
            () => (aircraft.GroundSpeed < AtRestKts) && (aircraft.Phases?.CurrentPhase is HoldingInPositionPhase or AtParkingPhase),
            maxSeconds,
            null
        );

    [Fact]
    public void TaxiTo5A_FromD2LeadOut_EndsAlongT5A()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot5A = ground.Layout.FindSpotNodeByName("5A");
        Assert.True(spot5A is not null, "SFO layout has no spot named '5A'");

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "SKW1", "E75L", "D2");
        CommandResult result = ground.Engine.SendCommand("SKW1", "TAXI $5A");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        AssertNoFreeSpaceLegIntoSpot(route, spot5A);

        int stopped = TickToRest(ground.Engine, aircraft, maxSeconds: 400);
        Assert.True(stopped > 0, "SKW1 never came to rest at spot 5A");
        AssertRestingOnSpotAlongLane(aircraft, spot5A, "T5A");
    }

    [Fact]
    public void TaxiT7aToSpot7A_FromOffLane_EndsAlongT7A()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7A = ground.Layout.FindSpotNodeByName("7A");
        Assert.True(spot7A is not null, "SFO layout has no spot named '7A'");

        (GroundNode abeam, double laneBearing) = MidLanePoint(ground.Layout, spot7A, "T7A");
        LatLon offLane = GeoMath.ProjectPoint(abeam.Position, new TrueHeading((laneBearing + 90.0) % 360.0), 115.0 / GeoMath.FeetPerNm);
        output.WriteLine($"abeam #{abeam.Id}, lane bearing {laneBearing:F0}, spawning 115 ft off it");

        AircraftState aircraft = SpawnOffGraph(ground, "SKW2", "CRJ2", (offLane, new TrueHeading((laneBearing + 47.0) % 360.0)));
        CommandResult result = ground.Engine.SendCommand("SKW2", "TAXI T7A $7A");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        AssertNoFreeSpaceLegIntoSpot(route, spot7A);

        int stopped = TickToRest(ground.Engine, aircraft, maxSeconds: 400);
        Assert.True(stopped > 0, "SKW2 never came to rest at spot 7A");
        AssertRestingOnSpotAlongLane(aircraft, spot7A, "T7A");
    }

    [Fact]
    public void TaxiSpot7A_To7B_EndsAlongT7B()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        SfoGround ground = built.Value;
        GroundNode? spot7B = ground.Layout.FindSpotNodeByName("7B");
        Assert.True(spot7B is not null, "SFO layout has no spot named '7B'");

        AircraftState aircraft = SfoGroundHarness.SpawnAtSpot(ground, "SKW3", "CRJ2", "7A");
        CommandResult result = ground.Engine.SendCommand("SKW3", "TAXI $7B");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        AssertNoFreeSpaceLegIntoSpot(route, spot7B);

        int stopped = TickToRest(ground.Engine, aircraft, maxSeconds: 400);
        Assert.True(stopped > 0, "SKW3 never came to rest at spot 7B");
        AssertRestingOnSpotAlongLane(aircraft, spot7B, "T7B");
    }

    /// <summary>The live case: SKW3396 parked at gate D2 in the recorded session, cleared to spot 5A.</summary>
    [Fact]
    public void Replay_Skw3396_TaxiTo5A_EndsAlongT5A()
    {
        SessionRecording? recording = RecordingLoader.Load(RecordingPath);
        if (recording is null)
        {
            return;
        }

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData);
        engine.Replay(recording, 990);

        AircraftState? aircraft = engine.FindAircraft("SKW3396");
        Assert.NotNull(aircraft);

        // Isolate the subject: the recorded session packs traffic around the D2 alley, and a ground conflict
        // there stalls the taxi and masks the arrival geometry under test.
        foreach (AircraftState other in engine.World.GetSnapshot())
        {
            if (!string.Equals(other.Callsign, "SKW3396", StringComparison.Ordinal))
            {
                engine.World.RemoveAircraft(other.Callsign);
            }
        }

        AirportGroundLayout? layout = engine.World.GroundLayout;
        Assert.NotNull(layout);
        GroundNode? spot5A = layout.FindSpotNodeByName("5A");
        Assert.True(spot5A is not null, "SFO layout has no spot named '5A'");

        CommandResult result = engine.SendCommand("SKW3396", "TAXI $5A");
        output.WriteLine($"result: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        SfoGroundHarness.DumpRoute(output, route);
        AssertNoFreeSpaceLegIntoSpot(route, spot5A);

        // The graph route up T5A is longer than the free-space cut it replaces, so the budget is the taxi's,
        // not a fixed 90 s: SKW3396 is still rolling the last 80 ft at t=90.
        int stopped = TickToRest(engine, aircraft, maxSeconds: 300);
        Assert.True(stopped > 0, "SKW3396 never came to rest at spot 5A");
        AssertRestingOnSpotAlongLane(aircraft, spot5A, "T5A");
    }

    /// <summary>
    /// The run a ramp cut must leave into a spot is one fuselage, not a flat 100 ft: a landing point that
    /// gives a 106 ft regional jet room to straighten out leaves a 242 ft B77W still crossing its own lane
    /// when its nose reaches the marking. Driven from a landing node the SFO graph really offers between the
    /// two lengths, resolved from the layout rather than named.
    /// </summary>
    [Fact]
    public void SpotAlignmentRun_IsOneFuselage_NotAFlatHundredFeet()
    {
        SfoGround? built = SfoGroundHarness.Build(output, autoCross: true);
        if (built is null)
        {
            return;
        }

        AirportGroundLayout layout = built.Value.Layout;
        double widebodyFt = FaaAircraftDatabase.Get("B77W")?.LengthFt ?? 0;
        Assert.True(widebodyFt > 200.0, $"B77W length came back as {widebodyFt:F0} ft");

        (GroundNode Spot, string Lane, TaxiRoute Tail)? found = FindShortLaneApproach(layout, widebodyFt);
        if (found is null)
        {
            output.WriteLine($"no SFO spot lane offers a landing node between {RunFloorFt:F0} and {widebodyFt:F0} ft of run — nothing to compare");
            return;
        }

        (GroundNode spot, string lane, TaxiRoute tail) = found.Value;
        output.WriteLine($"spot {spot.Name} on {lane}: landing node gives {tail.TotalDistanceFt:F0} ft of run");

        Assert.True(
            RampLaneReposition.EntersSpotAlongItsLane(spot, lane, tail, RunFloorFt),
            $"{tail.TotalDistanceFt:F0} ft is enough run for a {RunFloorFt:F0} ft aircraft"
        );
        Assert.False(
            RampLaneReposition.EntersSpotAlongItsLane(spot, lane, tail, widebodyFt),
            $"{tail.TotalDistanceFt:F0} ft of run was accepted for a {widebodyFt:F0} ft B77W"
        );
    }

    /// <summary>
    /// The first spot in the layout whose own lane offers a graph approach of between <see cref="RunFloorFt"/>
    /// and <paramref name="widebodyFt"/> feet of run — the window where the flat 100 ft rule and the
    /// one-fuselage rule disagree. Bounded at <see cref="MaxLaneCandidates"/> A* searches: SFO has enough
    /// spots, lanes and nodes that an exhaustive sweep is minutes of pathfinding on the no-match path, and a
    /// window this wide is either hit early or not there.
    /// </summary>
    /// <param name="layout">SFO ground layout.</param>
    /// <param name="widebodyFt">Upper bound on the run, in feet.</param>
    /// <returns>The spot, its lane, and the graph tail into it; null when the layout offers none.</returns>
    private static (GroundNode Spot, string Lane, TaxiRoute Tail)? FindShortLaneApproach(AirportGroundLayout layout, double widebodyFt)
    {
        int tried = 0;
        foreach (GroundNode spot in layout.Nodes.Values.Where(n => n.Type == GroundNodeType.Spot))
        {
            foreach (string lane in spot.Edges.Where(e => e is GroundEdge).Select(e => e.TaxiwayName).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (GroundNode from in layout.GetNodesOnTaxiway(lane).Where(n => n.Id != spot.Id))
                {
                    if (++tried > MaxLaneCandidates)
                    {
                        return null;
                    }

                    TaxiRoute? tail = TaxiPathfinder.FindRoute(layout, from.Id, spot.Id, AircraftCategory.Jet);
                    if (
                        (tail is null)
                        || (tail.Segments.Count == 0)
                        || (tail.TotalDistanceFt < RunFloorFt)
                        || (tail.TotalDistanceFt >= widebodyFt)
                        || !tail.Segments[^1].Edge.Edge.MatchesTaxiway(lane)
                        || (tail.Segments[^1].ToNodeId != spot.Id)
                    )
                    {
                        continue;
                    }

                    return (spot, lane, tail);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A point part-way down the spot's lane and the direction the lane runs there: the aircraft in the
    /// off-lane case sits abeam it, on open apron, with the lane's painted line beside it.
    /// </summary>
    /// <param name="layout">SFO ground layout.</param>
    /// <param name="spot">The spot whose lane is wanted.</param>
    /// <param name="lane">The lane name.</param>
    /// <returns>The node abeam which to place the aircraft, and the lane's true bearing there.</returns>
    private static (GroundNode Abeam, double LaneBearing) MidLanePoint(AirportGroundLayout layout, GroundNode spot, string lane)
    {
        var onLane = layout
            .GetNodesOnTaxiway(lane)
            .Where(n => (n.Id != spot.Id) && n.Edges.Any(e => (e is GroundEdge) && e.MatchesTaxiway(lane)))
            .OrderBy(n => GeoMath.DistanceNm(spot.Position, n.Position))
            .ToList();
        if (onLane.Count == 0)
        {
            throw new InvalidOperationException($"SFO layout has no node on lane '{lane}' besides spot '{spot.Name}'");
        }

        GroundNode abeam = onLane[onLane.Count / 2];
        IGroundEdge edge = abeam.Edges.First(e => (e is GroundEdge) && e.MatchesTaxiway(lane));
        return (abeam, GeoMath.BearingTo(abeam.Position, edge.OtherNode(abeam).Position));
    }

    /// <summary>
    /// Spawns a stationary aircraft at a position that is not a graph node — the harness places aircraft on
    /// nodes, and this case needs one sitting out on the apron beside its lane.
    /// </summary>
    /// <param name="ground">Engine + layout.</param>
    /// <param name="callsign">Callsign to spawn under.</param>
    /// <param name="type">ICAO aircraft type.</param>
    /// <param name="pose">Position and true heading to place it at.</param>
    /// <returns>The spawned aircraft.</returns>
    private static AircraftState SpawnOffGraph(SfoGround ground, string callsign, string type, (LatLon Position, TrueHeading Heading) pose)
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = type,
            Position = pose.Position,
            TrueHeading = pose.Heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ground.Layout));
        aircraft.Ground.Layout = ground.Layout;
        ground.Engine.World.AddAircraft(aircraft);
        return aircraft;
    }
}
