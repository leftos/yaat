using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>Flies OAK 28L closed traffic from the takeoff roll through the base turn and judges the flown track.</summary>
public class PatternCompressionFlownTests
{
    private const int HangGuardTicks = 3600;

    /// <summary>One circuit flown from its liftoff to its final, with the fastest indicated airspeed reached airborne.</summary>
    private sealed record Flight(
        List<LatLon> Track,
        PatternWaypoints Waypoints,
        double WidestDownwindNm,
        double SpeedAtCrosswindTurnKts,
        double MaxAirborneIasKts,
        string FastestPhase
    );

    /// <summary>What a tick flies under: the room's active runways and the weather.</summary>
    private sealed record Conditions(ActiveRunways ActiveRunways, WeatherProfile? Weather)
    {
        public static Conditions Calm => new(ActiveRunways.Empty, null);
    }

    private static PhaseContext Ctx(AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout, ActiveRunways activeRunways) =>
        new()
        {
            ActiveRunways = activeRunways,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 0.25,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            GroundLayout = layout,
            AircraftLookup = _ => null,
            Logger = NullLogger.Instance,
        };

    /// <summary>The circuits flown, and the fastest airborne indicated airspeed of each touch-and-go between them.</summary>
    private sealed record FlownCircuits(List<Flight> Laps, List<double> TouchAndGoMaxIasKts);

    private static Flight FlyClosedTraffic(string type) => FlyCircuits(type, 1).Laps[0];

    /// <summary>Flies <paramref name="circuits"/> touch-and-go circuits of OAK 28L left traffic, each up to its final.</summary>
    private static FlownCircuits FlyCircuits(string type, int circuits)
    {
        (AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout) = ClearedForClosedTraffic(type);
        var recorder = new TickRecorder(ac);
        var flights = new List<Flight>();
        var touchAndGoMaxIasKts = new List<double>();
        int tick = 0;
        while (flights.Count < circuits)
        {
            flights.Add(FlyToFinal(ac, rwy, layout, recorder, ref tick));
            if (flights.Count < circuits)
            {
                touchAndGoMaxIasKts.Add(FlyOffTheFinal(ac, rwy, layout, recorder, ref tick));
            }
        }

        return new FlownCircuits(flights, touchAndGoMaxIasKts);
    }

    /// <summary>An aircraft of <paramref name="type"/> on OAK 28L, cleared for left closed traffic and started.</summary>
    private static (AircraftState Ac, RunwayInfo Rwy, AirportGroundLayout Layout) ClearedForClosedTraffic(string type) =>
        ClearedForClosedTraffic(type, null);

    /// <summary>
    /// <see cref="ClearedForClosedTraffic(string)"/> with the surface wind of <paramref name="weather"/> already on the aircraft,
    /// so the circuit is planned with it.
    /// </summary>
    private static (AircraftState Ac, RunwayInfo Rwy, AirportGroundLayout Layout) ClearedForClosedTraffic(string type, WeatherProfile? weather)
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo? rwy = TestVnasData.NavigationDb!.GetRunway("KOAK", "28L");
        Assert.NotNull(rwy);
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        var ac = new AircraftState
        {
            Callsign = "N123",
            AircraftType = type,
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = rwy.TrueHeading,
            TrueTrack = rwy.TrueHeading,
            Altitude = rwy.ElevationFt,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK" },
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        ac.Ground.Layout = layout;
        ac.WindComponents = WindInterpolator.GetWindComponents(weather, ac.Altitude, 0, 0);
        ac.Phases.Add(new TakeoffPhase());
        DepartureClearanceHandler.ApplyClosedTraffic(
            new ClosedTrafficDeparture(PatternDirection.Left, null, null),
            ac,
            ac.Phases,
            rwy,
            false,
            ActiveRunways.Empty
        );
        ac.Phases.Start(Ctx(ac, rwy, layout, ActiveRunways.Empty));
        return (ac, rwy, layout);
    }

    private static Flight FlyToFinal(AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout, TickRecorder recorder, ref int tick)
    {
        // A touch-and-go needs its clearance on every lap (the next circuit clears it); without one the final goes around at 200 ft AGL.
        ac.Phases!.LandingClearance = ClearanceType.ClearedTouchAndGo;
        PatternWaypoints waypoints = AirborneFollowHelper.PatternWaypointsOf(
            ac.Phases!.Phases.Skip(ac.Phases.CurrentIndex).First(phase => phase is UpwindPhase)
        )!;
        var track = new List<LatLon>();
        var threshold = new LatLon(waypoints.ThresholdLat, waypoints.ThresholdLon);
        double widestDownwindNm = 0;
        double speedAtCrosswindTurnKts = double.NaN;
        double maxAirborneIasKts = 0;
        string fastestPhase = "";
        for (int guard = 0; guard < HangGuardTicks; guard++)
        {
            Step(ac, rwy, layout, recorder, ref tick);
            if (!ac.IsOnGround)
            {
                track.Add(ac.Position);
                if (ac.IndicatedAirspeed > maxAirborneIasKts)
                {
                    maxAirborneIasKts = ac.IndicatedAirspeed;
                    fastestPhase = ac.Phases!.CurrentPhase?.Name ?? "none";
                }
            }

            if (double.IsNaN(speedAtCrosswindTurnKts) && (ac.Phases!.CurrentPhase is CrosswindPhase))
            {
                speedAtCrosswindTurnKts = ac.IndicatedAirspeed;
            }

            if (ac.Phases!.CurrentPhase is DownwindPhase)
            {
                widestDownwindNm = Math.Max(
                    widestDownwindNm,
                    Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, waypoints.FinalHeading))
                );
            }

            if (ac.Phases!.CurrentPhase is FinalApproachPhase)
            {
                return new Flight(track, waypoints, widestDownwindNm, speedAtCrosswindTurnKts, maxAirborneIasKts, fastestPhase);
            }
        }

        Assert.Fail($"hung: {ac.AircraftType} never reached final, last phase {ac.Phases!.CurrentPhase?.Name}");
        throw new InvalidOperationException();
    }

    /// <summary>
    /// Flies the final and the touch-and-go until the next circuit's upwind is flying, and returns the fastest airborne indicated
    /// airspeed of the touch-and-go.
    /// </summary>
    private static double FlyOffTheFinal(AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout, TickRecorder recorder, ref int tick)
    {
        double maxTouchAndGoIasKts = 0;
        for (int guard = 0; guard < HangGuardTicks; guard++)
        {
            Step(ac, rwy, layout, recorder, ref tick);
            Assert.False(ac.Phases!.CurrentPhase is GoAroundPhase, $"{ac.AircraftType} went around instead of flying its touch-and-go");
            if ((ac.Phases.CurrentPhase is TouchAndGoPhase) && !ac.IsOnGround)
            {
                maxTouchAndGoIasKts = Math.Max(maxTouchAndGoIasKts, ac.IndicatedAirspeed);
            }

            if (ac.Phases.CurrentPhase is UpwindPhase)
            {
                return maxTouchAndGoIasKts;
            }
        }

        Assert.Fail($"hung: {ac.AircraftType} never started a second circuit, last phase {ac.Phases!.CurrentPhase?.Name}");
        throw new InvalidOperationException();
    }

    private static void Step(AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout, TickRecorder recorder, ref int tick)
    {
        tick++;
        StepUnder(ac, rwy, layout, Conditions.Calm);
        recorder.Record(tick);
    }

    private static void StepUnder(AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout, Conditions conditions)
    {
        PhaseContext ctx = Ctx(ac, rwy, layout, conditions.ActiveRunways);
        FlightPhysics.Update(ac, ctx.DeltaSeconds, aircraftLookup: null, conditions.Weather, simTimeSeconds: 0);
        PhaseRunner.Tick(ac, ctx);
    }

    /// <summary>Ticks under <paramref name="conditions"/> until <paramref name="reached"/> holds, failing as hung past the guard.</summary>
    private static void FlyUntil(
        AircraftState ac,
        (RunwayInfo Rwy, AirportGroundLayout Layout) field,
        Conditions conditions,
        List<LatLon> track,
        Func<Phase?, bool> reached
    )
    {
        for (int guard = 0; guard < HangGuardTicks; guard++)
        {
            StepUnder(ac, field.Rwy, field.Layout, conditions);
            if (!ac.IsOnGround)
            {
                track.Add(ac.Position);
            }

            if (reached(ac.Phases!.CurrentPhase))
            {
                return;
            }
        }

        Assert.Fail($"hung: {ac.AircraftType} never reached the awaited phase, last phase {ac.Phases!.CurrentPhase?.Name}");
    }

    private static int CrossingWarnings(AircraftState ac) => ac.PendingWarnings.Count(w => w.Contains("crosses runway", StringComparison.Ordinal));

    /// <summary>
    /// ARWY OAK 28L 28R mid-downwind takes runway 30 out of the active list: the circuit being flown keeps its compression, the
    /// next one is judged afresh against the new list and flies its normal size, and no crossing is warned of.
    /// </summary>
    [Fact]
    public void Oak28LMlt_PC12_ArwyMidDownwind_JudgesTheNextCircuitAfresh()
    {
        (AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout) = ClearedForClosedTraffic("PC12");
        ac.Phases!.LandingClearance = ClearanceType.ClearedTouchAndGo;
        var track = new List<LatLon>();
        FlyUntil(ac, (rwy, layout), Conditions.Calm, track, phase => phase is DownwindPhase);
        PatternWaypoints downwind = AirborneFollowHelper.PatternWaypointsOf(ac.Phases!.CurrentPhase)!;
        Assert.NotNull(downwind.CompressedDownwindSpeedKts);

        var arwy = new Conditions(
            ActiveRunways.Empty.With("OAK", [new ActiveRunway("28L", ActiveRunwayUse.Both), new ActiveRunway("28R", ActiveRunwayUse.Both)]),
            null
        );
        FlyUntil(ac, (rwy, layout), arwy, track, phase => phase is BasePhase);
        PatternWaypoints inFlightBase = AirborneFollowHelper.PatternWaypointsOf(ac.Phases!.CurrentPhase)!;
        Assert.Equal(downwind.CompressedDownwindSpeedKts, inFlightBase.CompressedDownwindSpeedKts);
        Assert.Equal(downwind.CompressedTurnRateDegPerSec, inFlightBase.CompressedTurnRateDegPerSec);
        Assert.Equal(downwind.PatternSizeNm, inFlightBase.PatternSizeNm, 9);
        Assert.Equal(downwind.BaseTurnLat, inFlightBase.BaseTurnLat, 9);
        Assert.Equal(downwind.BaseTurnLon, inFlightBase.BaseTurnLon, 9);
        FlyUntil(ac, (rwy, layout), arwy, track, phase => phase is UpwindPhase);

        PatternWaypoints next = AirborneFollowHelper.PatternWaypointsOf(ac.Phases!.CurrentPhase)!;
        Assert.Null(next.CompressedDownwindSpeedKts);
        Assert.Null(next.RunwayCrossing);
        Assert.Equal(0, CrossingWarnings(ac));
    }

    /// <summary>
    /// A circuit planned and flown in a 15 kt wind: from 190 it pushes the circuit away from runway 30, from 020 toward it. A
    /// circuit the planner still compresses keeps its flown track clear of runway 30's corridor; one it cannot falls back to
    /// its normal size and warns.
    /// </summary>
    [Theory]
    [InlineData("PC12", 190, false)]
    [InlineData("PC12", 20, false)]
    [InlineData("C208", 190, true)]
    [InlineData("C208", 20, true)]
    public void Oak28LMlt_InA15KtWind_CompressedTrackStaysClearOfRunway30OrWarns(string type, int windFromDeg, bool compresses)
    {
        var weather = new WeatherProfile { WindLayers = [new WindLayer { Direction = windFromDeg, Speed = 15 }] };
        (AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout) = ClearedForClosedTraffic(type, weather);
        Assert.Equal(15, ac.WindSpeedKts, 6);
        PatternWaypoints circuit = PatternCompression.UpcomingCircuit(ac.Phases)!;
        var track = new List<LatLon>();

        FlyUntil(ac, (rwy, layout), new Conditions(ActiveRunways.Empty, weather), track, phase => phase is FinalApproachPhase);

        if (!compresses)
        {
            AircraftCategory category = AircraftCategorization.Categorize(type);
            Assert.Null(circuit.CompressedDownwindSpeedKts);
            Assert.Equal(PatternGeometry.MinFlyablePatternSizeNm(type, category, 15), circuit.PatternSizeNm, 2);
            Assert.Equal("30", circuit.RunwayCrossing?.CrossedRunway);
            Assert.Equal(1, CrossingWarnings(ac));
            return;
        }

        Assert.NotNull(circuit.CompressedDownwindSpeedKts);
        Assert.Null(circuit.RunwayCrossing);
        PatternCompressionTests.AssertClearOfRunway30(track);
    }

    [Fact]
    public void Oak28LMlt_PC12_FlownTrackStaysClearOfRunway30()
    {
        Flight flight = FlyClosedTraffic("PC12");

        Assert.NotNull(flight.Waypoints.CompressedDownwindSpeedKts);
        PatternCompressionTests.AssertClearOfRunway30(flight.Track);
        Assert.InRange(flight.WidestDownwindNm, flight.Waypoints.PatternSizeNm - 0.1, flight.Waypoints.PatternSizeNm + 0.1);
        Assert.Equal(flight.Waypoints.CompressedDownwindSpeedKts.Value, flight.SpeedAtCrosswindTurnKts, 0);
    }

    [Theory]
    [InlineData("PC12")]
    [InlineData("C208")]
    public void Oak28LMlt_CompressedCircuit_NeverFliesFasterThanItsCapOnEitherLap(string type)
    {
        FlownCircuits flown = FlyCircuits(type, 2);
        AircraftCategory category = AircraftCategorization.Categorize(type);

        foreach (Flight lap in flown.Laps)
        {
            Assert.NotNull(lap.Waypoints.CompressedDownwindSpeedKts);
            double capKts = PatternCompression.TakeoffClimbSpeed(lap.Waypoints, type, category);
            Assert.True(
                lap.MaxAirborneIasKts <= capKts + 0.01,
                $"{type} lap {flown.Laps.IndexOf(lap) + 1} flew {lap.MaxAirborneIasKts:F1} kt in {lap.FastestPhase} against its {capKts:F1} kt cap"
            );
        }

        // The next circuit is built when the touch-and-go completes, so its climb holds the cap of the circuit just flown.
        double touchAndGoCapKts = PatternCompression.TakeoffClimbSpeed(flown.Laps[0].Waypoints, type, category);
        Assert.True(
            flown.TouchAndGoMaxIasKts[0] <= touchAndGoCapKts + 0.01,
            $"{type} touch-and-go climbed at {flown.TouchAndGoMaxIasKts[0]:F1} kt against its {touchAndGoCapKts:F1} kt cap"
        );
        PatternCompressionTests.AssertClearOfRunway30(flown.Laps[1].Track);
    }

    [Fact]
    public void Oak28LMlt_DHC6_FlownTrackStaysClearOfRunway30()
    {
        Flight flight = FlyClosedTraffic("DHC6");

        Assert.NotNull(flight.Waypoints.CompressedDownwindSpeedKts);
        PatternCompressionTests.AssertClearOfRunway30(flight.Track);
    }

    /// <summary>
    /// The SF50 climbs slowly at its compressed speed and turns crosswind late, at the 300-ft gate below pattern altitude. Only
    /// a clean circuit stays silent: it either compresses with a flown track clear of runway 30's corridor, or flies its normal
    /// circuit and warns once of the crossing.
    /// </summary>
    [Fact]
    public void Oak28LMlt_SF50_FlownTrackStaysClearOfRunway30CorridorOrWarns() => AssertCompressedAndClearOrUncompressedAndWarned("SF50", null);

    /// <summary>
    /// The planner judges a compressed circuit with its crosswind leg at the departure end plus
    /// <see cref="PatternCompression.TurnPointPastDepartureEndNm"/>, while the upwind turns at the 300-ft gate below pattern
    /// altitude. A type whose flown turn starts later than the planner assumed (beyond 0.05 nm) flies a circuit it never judged.
    /// </summary>
    [Theory]
    [InlineData("PC12")]
    [InlineData("C208")]
    [InlineData("DHC6")]
    public void Oak28LMlt_EachCompressedType_FlownTurnAtOrBeforePlannedTurn(string type)
    {
        (AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout) = ClearedForClosedTraffic(type);
        PatternWaypoints circuit = PatternCompression.UpcomingCircuit(ac.Phases)!;
        Assert.NotNull(circuit.CompressedDownwindSpeedKts);
        var threshold = new LatLon(circuit.ThresholdLat, circuit.ThresholdLon);
        double departureEndAlongNm = GeoMath.AlongTrackDistanceNm(
            new LatLon(circuit.DepartureEndLat, circuit.DepartureEndLon),
            threshold,
            circuit.UpwindHeading
        );
        double plannedNm = departureEndAlongNm + PatternCompression.TurnPointPastDepartureEndNm;

        FlyUntil(ac, (rwy, layout), Conditions.Calm, [], phase => phase is CrosswindPhase);

        double flownNm = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, circuit.UpwindHeading);
        Assert.True(flownNm <= plannedNm + 0.05, $"{type} turned crosswind at {flownNm:F3} nm along, planned {plannedNm:F3} nm");
    }

    [Theory]
    [InlineData("SF50")]
    [InlineData("PC12")]
    public void Oak28LMlt_SF50AndPC12_TailwindUpwind_CompressedAndClearOrOneWarning(string type) =>
        AssertCompressedAndClearOrUncompressedAndWarned(type, new WeatherProfile { WindLayers = [new WindLayer { Direction = 100, Speed = 10 }] });

    [Fact]
    public void Oak28LMlt_PC12_HeadwindUpwind_CompressedAndClear() =>
        AssertCompressedAndClearOrUncompressedAndWarned("PC12", new WeatherProfile { WindLayers = [new WindLayer { Direction = 280, Speed = 15 }] });

    /// <summary>
    /// Flies one circuit of <paramref name="type"/> to its final under <paramref name="weather"/>: a compressed circuit warns of
    /// nothing and its flown track stays clear of runway 30's protected corridor (the planner's own predicate); a normal one
    /// warns exactly once. Never silent contact.
    /// </summary>
    private static void AssertCompressedAndClearOrUncompressedAndWarned(string type, WeatherProfile? weather)
    {
        (AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout) = ClearedForClosedTraffic(type, weather);
        PatternWaypoints circuit = PatternCompression.UpcomingCircuit(ac.Phases)!;
        var track = new List<LatLon>();

        FlyUntil(ac, (rwy, layout), new Conditions(ActiveRunways.Empty, weather), track, phase => phase is FinalApproachPhase);

        if (circuit.CompressedDownwindSpeedKts is null)
        {
            Assert.Equal("30", circuit.RunwayCrossing?.CrossedRunway);
            Assert.Equal(1, CrossingWarnings(ac));
            return;
        }

        RunwayInfo? r30 = TestVnasData.NavigationDb!.GetRunway("KOAK", "30");
        Assert.NotNull(r30);
        Assert.Equal(0, CrossingWarnings(ac));
        Assert.False(
            PatternCompression.TouchesProtectedRunway(rwy, new LatLon(circuit.ThresholdLat, circuit.ThresholdLon), r30, track),
            $"{type} flew a compressed circuit ({circuit.PatternSizeNm:F3} nm) into runway 30's protected corridor without a warning"
        );
    }

    [Fact]
    public void Oak28LMlt_PC12_SnapshotMidDownwind_KeepsTheCompressedCircuit()
    {
        (AircraftState ac, RunwayInfo rwy, AirportGroundLayout layout) = ClearedForClosedTraffic("PC12");
        var recorder = new TickRecorder(ac);
        int tick = 0;
        for (int guard = 0; (guard < HangGuardTicks) && (ac.Phases!.CurrentPhase is not DownwindPhase); guard++)
        {
            Step(ac, rwy, layout, recorder, ref tick);
        }

        Assert.IsType<DownwindPhase>(ac.Phases!.CurrentPhase);

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), layout);

        PatternWaypoints before = AirborneFollowHelper.PatternWaypointsOf(ac.Phases.CurrentPhase)!;
        PatternWaypoints after = AirborneFollowHelper.PatternWaypointsOf(restored.Phases!.CurrentPhase)!;
        Assert.NotNull(before.CompressedDownwindSpeedKts);
        Assert.Equal(before.CompressedDownwindSpeedKts, after.CompressedDownwindSpeedKts);
        Assert.Equal(before.CompressedBaseSpeedKts, after.CompressedBaseSpeedKts);
        Assert.Equal(before.CompressedTurnRateDegPerSec, after.CompressedTurnRateDegPerSec);
        Assert.Equal(before.PatternSizeNm, after.PatternSizeNm, 9);
    }

    [Fact]
    public void Oak28LMlt_B738_FlownTrackIsUnchanged()
    {
        Flight flight = FlyClosedTraffic("B738");

        Assert.Null(flight.Waypoints.CompressedDownwindSpeedKts);
        // A normal crosswind has no turn lead, so the downwind settles about one turn radius outside its planned width.
        double radiusNm = BasePhase.TurnRadiusNm(AircraftPerformance.DownwindSpeed("B738", AircraftCategory.Jet), AircraftCategory.Jet);
        double settledNm = flight.Waypoints.PatternSizeNm + (0.8 * radiusNm);
        Assert.True(
            flight.WidestDownwindNm >= settledNm,
            $"a normal circuit settles outside its width: {flight.WidestDownwindNm:F2} nm against {settledNm:F2} nm"
        );
    }
}
