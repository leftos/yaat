using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// Whether a runway transition is a parallel transition (continue the upwind, turn crosswind past both
/// departure ends, AIM 4-3-3 FIG 4-3-3 keys 7 and 8) or a crossing transition (midfield crossing) depends
/// on the two runways being parallel — same airport, headings within
/// <see cref="RunwayGeometry.MaxParallelHeadingDeltaDeg"/> — and not on their spacing. The side-step
/// spacing bound (AIM 5-4-19) stays on the EF side-step path alone.
///
/// A parallel transition anchors its crosswind turn on the pattern runway's centerline abeam the farther
/// departure end, so the downwind starts exactly one pattern width from that centerline however far apart
/// the runways are: KMSP 30L/30R, measured from the pinned navdata at test time.
/// </summary>
public class ParallelRunwayClassificationTests
{
    private const string WidePairAirport = "KMSP";
    private const double GeometryToleranceNm = 0.05;

    private static readonly (string Airport, string Flown, string Pattern)[] WideParallelCandidates =
    [
        ("KMSP", "30L", "30R"),
        ("KPHX", "08", "07R"),
        ("KDTW", "21L", "21R"),
        ("KIAH", "26L", "26R"),
    ];

    private readonly ITestOutputHelper _output;

    public ParallelRunwayClassificationTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void WideParallels_ClosedTrafficOffOneForTheOther_BuildsTheParallelTransition()
    {
        (RunwayInfo flown, RunwayInfo pattern, double spacingFt) = RequireWideParallelPair();
        _output.WriteLine($"wide pair {flown.AirportId} {flown.Designator}/{pattern.Designator}: {spacingFt:F0} ft between centerlines");

        List<Phase> phases = BuildTransition(flown, pattern, AircraftCategory.Jet, "B738", PatternDirection.Right);

        Assert.IsType<UpwindPhase>(phases[0]);
        Assert.IsType<CrosswindPhase>(phases[1]);
        Assert.DoesNotContain(phases, p => p is MidfieldCrossingPhase);
        AssertTransitionGeometry(flown, pattern, phases, "B738 right traffic");
    }

    [Theory]
    [InlineData("C172", AircraftCategory.Piston, "30L", "30R", PatternDirection.Right)]
    [InlineData("C172", AircraftCategory.Piston, "30L", "30R", PatternDirection.Left)]
    [InlineData("B738", AircraftCategory.Jet, "30R", "30L", PatternDirection.Right)]
    [InlineData("B738", AircraftCategory.Jet, "30R", "30L", PatternDirection.Left)]
    public void TransitionTurnPoint_KeepsTheDownwindOnePatternWidthFromThePatternCenterline(
        string aircraftType,
        AircraftCategory category,
        string flownId,
        string patternId,
        PatternDirection direction
    )
    {
        RunwayInfo flown = RequireRunway(WidePairAirport, flownId);
        RunwayInfo pattern = RequireRunway(WidePairAirport, patternId);
        List<Phase> phases = BuildTransition(flown, pattern, category, aircraftType, direction);

        Assert.DoesNotContain(phases, p => p is MidfieldCrossingPhase);
        AssertTransitionGeometry(flown, pattern, phases, $"{aircraftType} {flownId} to {patternId} {direction}");
    }

    [Fact]
    public void ParallelPair_PatternEndFarther_AnchorsAtThatRunwaysExactPavementEnd()
    {
        RunwayInfo flown = RequireRunway(WidePairAirport, "30R");
        RunwayInfo pattern = RequireRunway(WidePairAirport, "30L");
        var patternThreshold = new LatLon(pattern.ThresholdLatitude, pattern.ThresholdLongitude);
        double patternAlongNm = GeoMath.AlongTrackDistanceNm(
            new LatLon(pattern.EndLatitude, pattern.EndLongitude),
            patternThreshold,
            pattern.TrueHeading
        );
        double flownAlongNm = GeoMath.AlongTrackDistanceNm(new LatLon(flown.EndLatitude, flown.EndLongitude), patternThreshold, pattern.TrueHeading);
        Assert.True(RunwayGeometry.AreParallelRunways(flown, pattern), "30L and 30R are parallel");
        Assert.True(patternAlongNm > flownAlongNm, "30L's own departure end is the farther one along 30L's heading");

        LatLon anchor = PatternGeometry.TransitionDepartureEnd(flown, pattern);

        Assert.Equal(pattern.EndLatitude, anchor.Lat, 6);
        Assert.Equal(pattern.EndLongitude, anchor.Lon, 6);
    }

    [Fact]
    public void CrossingPair_Oak28RTo33_BuildsTheCrossingTransition()
    {
        List<Phase> phases = BuildTransition(
            RequireRunway("KOAK", "28R"),
            RequireRunway("KOAK", "33"),
            AircraftCategory.Jet,
            "B738",
            PatternDirection.Right
        );

        Assert.IsType<UpwindPhase>(phases[0]);
        Assert.Contains(phases, p => p is MidfieldCrossingPhase);
    }

    [Fact]
    public void MixedAirportIds_KoakAndOak_AreTheSameAirport()
    {
        RunwayInfo flown = RequireRunway("KOAK", "28R");
        RunwayInfo pattern = WithAirportId(RequireRunway("KOAK", "28L"), "OAK");

        Assert.True(RunwayGeometry.AreParallelRunways(flown, pattern), "KOAK and OAK name one airport, so the pair is parallel");
        Assert.True(
            RunwayGeometry.AreCloseParallels(flown, pattern),
            "KOAK and OAK name one airport, so the close-parallel pair is still a side-step pair"
        );
    }

    [Fact]
    public void Sidestep_AtWideSpacing_IsNotOffered()
    {
        (RunwayInfo flown, RunwayInfo pattern, double spacingFt) = RequireWideParallelPair();
        AircraftState aircraft = MakeAircraftOnFinal(flown, altAgl: 2500, distFromThresholdNm: 8);
        var finalApproach = new FinalApproachPhase();
        aircraft.Phases!.Add(finalApproach);
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Start(BuildCtx(aircraft, flown));

        CommandResult result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            new EnterPatternRequest
            {
                RequestedDirection = PatternDirection.Left,
                EntryLeg = PatternEntryLeg.Final,
                RunwayId = pattern.Designator,
                FinalDistanceNm = null,
                ActiveRunways = ActiveRunways.Empty,
                WarnRunwayCrossing = true,
                GroundLayout = null,
            }
        );

        _output.WriteLine($"EF {flown.Designator} to {pattern.Designator} at {spacingFt:F0} ft: {result.Message}");
        Assert.DoesNotContain("sidestep", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The crosswind turn sits on the pattern runway's centerline, past both runways' departure ends, and the
    /// downwind starts one pattern width from that centerline (FIG 4-3-3 keys 7 and 8: no crossing over the
    /// other runway's departure path, and a downwind flown on the pattern runway's own width).
    /// </summary>
    private void AssertTransitionGeometry(RunwayInfo flown, RunwayInfo pattern, List<Phase> phases, string label)
    {
        UpwindPhase upwind = Assert.IsType<UpwindPhase>(phases[0]);
        PatternWaypoints waypoints = Assert.IsType<PatternWaypoints>(upwind.Waypoints);
        var patternThreshold = new LatLon(pattern.ThresholdLatitude, pattern.ThresholdLongitude);
        var turn = new LatLon(waypoints.CrosswindTurnLat, waypoints.CrosswindTurnLon);
        var downwindStart = new LatLon(waypoints.DownwindStartLat, waypoints.DownwindStartLon);
        double turnOffsetNm = GeoMath.SignedCrossTrackDistanceNm(turn, patternThreshold, pattern.TrueHeading);
        double downwindOffsetNm = GeoMath.SignedCrossTrackDistanceNm(downwindStart, patternThreshold, pattern.TrueHeading);
        double turnAlongNm = GeoMath.AlongTrackDistanceNm(turn, patternThreshold, pattern.TrueHeading);
        double flownEndAlongNm = GeoMath.AlongTrackDistanceNm(
            new LatLon(flown.EndLatitude, flown.EndLongitude),
            patternThreshold,
            pattern.TrueHeading
        );
        double patternEndAlongNm = GeoMath.AlongTrackDistanceNm(
            new LatLon(pattern.EndLatitude, pattern.EndLongitude),
            patternThreshold,
            pattern.TrueHeading
        );
        double flownOffsetNm = GeoMath.SignedCrossTrackDistanceNm(
            new LatLon(flown.ThresholdLatitude, flown.ThresholdLongitude),
            patternThreshold,
            pattern.TrueHeading
        );

        _output.WriteLine(
            $"{label}: pattern width {waypoints.PatternSizeNm:F2} nm, flown runway {flownOffsetNm:+0.00;-0.00} nm off the pattern "
                + $"centerline, anchored at the {(flownEndAlongNm > patternEndAlongNm ? "flown" : "pattern")} departure end, "
                + $"turn {turnAlongNm:F2} nm along (flown end {flownEndAlongNm:F2}, pattern end {patternEndAlongNm:F2}), "
                + $"downwind start {downwindOffsetNm:+0.00;-0.00} nm off the pattern centerline"
        );

        Assert.InRange(Math.Abs(downwindOffsetNm), waypoints.PatternSizeNm - GeometryToleranceNm, waypoints.PatternSizeNm + GeometryToleranceNm);
        Assert.InRange(Math.Abs(turnOffsetNm), 0.0, 0.01);
        Assert.True(
            turnAlongNm >= (flownEndAlongNm - 0.01),
            $"the crosswind turn at {turnAlongNm:F2} nm starts before the flown runway's departure end at {flownEndAlongNm:F2}"
        );
        Assert.True(
            turnAlongNm >= (patternEndAlongNm - 0.01),
            $"the crosswind turn at {turnAlongNm:F2} nm starts before the pattern runway's departure end at {patternEndAlongNm:F2}"
        );
    }

    /// <summary>
    /// The first candidate pair 2,000-4,000 ft apart between centerlines, measured from the pinned navdata
    /// (<see cref="GeoMath.SignedCrossTrackDistanceNm"/> from the second runway's threshold to the first
    /// runway's centerline). Fails with every measurement when none qualifies.
    /// </summary>
    private static (RunwayInfo Flown, RunwayInfo Pattern, double SpacingFt) RequireWideParallelPair()
    {
        NavigationDatabase navDb = TestVnasData.NavigationDb ?? throw new InvalidOperationException("no test navdata");
        List<string> measured = [];
        foreach ((string airport, string flownId, string patternId) in WideParallelCandidates)
        {
            RunwayInfo? flown = navDb.GetRunway(airport, flownId);
            RunwayInfo? pattern = navDb.GetRunway(airport, patternId);
            if ((flown is null) || (pattern is null))
            {
                measured.Add($"{airport} {flownId}/{patternId}: not in the pinned navdata");
                continue;
            }

            double spacingFt = CenterlineSpacingFt(flown, pattern);
            double headingDelta = flown.TrueHeading.AbsAngleTo(pattern.TrueHeading);
            measured.Add($"{airport} {flownId}/{patternId}: {spacingFt:F0} ft, heading delta {headingDelta:F1}°");
            if ((spacingFt >= 2000.0) && (spacingFt <= 4000.0))
            {
                return (flown, pattern, spacingFt);
            }
        }

        throw new InvalidOperationException("no wide parallel pair measured 2,000-4,000 ft apart: " + string.Join("; ", measured));
    }

    private static double CenterlineSpacingFt(RunwayInfo flown, RunwayInfo pattern) =>
        Math.Abs(
            GeoMath.SignedCrossTrackDistanceNm(
                new LatLon(pattern.ThresholdLatitude, pattern.ThresholdLongitude),
                new LatLon(flown.ThresholdLatitude, flown.ThresholdLongitude),
                flown.TrueHeading
            )
        ) * GeoMath.FeetPerNm;

    private static RunwayInfo RequireRunway(string airport, string designator)
    {
        NavigationDatabase navDb = TestVnasData.NavigationDb ?? throw new InvalidOperationException("no test navdata");
        return navDb.GetRunway(airport, designator) ?? throw new InvalidOperationException($"{airport} {designator} is not in the pinned navdata");
    }

    /// <summary><paramref name="runway"/> — the same pavement from the pinned navdata — carrying <paramref name="airportId"/>.</summary>
    private static RunwayInfo WithAirportId(RunwayInfo runway, string airportId) =>
        new()
        {
            AirportId = airportId,
            Id = runway.Id,
            Designator = runway.Designator,
            Lat1 = runway.Lat1,
            Lon1 = runway.Lon1,
            Elevation1Ft = runway.Elevation1Ft,
            TrueHeading1 = runway.TrueHeading1,
            Lat2 = runway.Lat2,
            Lon2 = runway.Lon2,
            Elevation2Ft = runway.Elevation2Ft,
            TrueHeading2 = runway.TrueHeading2,
            WidthFt = runway.WidthFt,
            AirportElevationFt = runway.AirportElevationFt,
        };

    /// <summary>
    /// The closed-traffic departure's first circuit: a takeoff off <paramref name="flown"/> making traffic
    /// for <paramref name="pattern"/>.
    /// </summary>
    private static List<Phase> BuildTransition(
        RunwayInfo flown,
        RunwayInfo pattern,
        AircraftCategory category,
        string aircraftType,
        PatternDirection direction
    )
    {
        NavigationDatabase navDb = TestVnasData.NavigationDb ?? throw new InvalidOperationException("no test navdata");
        return PatternBuilder.BuildRunwayTransitionCircuit(
            new RunwayTransitionCircuitRequest
            {
                FlownRunway = flown,
                PatternRunway = pattern,
                Category = category,
                AircraftType = aircraftType,
                WindSpeedKt = 0,
                Direction = direction,
                TouchAndGo = true,
                PatternSizeNm = null,
                AltitudeOverrideFt = null,
                AirportRunways = navDb.GetRunways(flown.AirportId),
                FlownAuthoredRunway = null,
                PatternAuthoredRunway = null,
                ActiveRunways = ActiveRunways.Empty,
                Commanded = PatternCommanded.None,
            }
        );
    }

    private static AircraftState MakeAircraftOnFinal(RunwayInfo runway, double altAgl, double distFromThresholdNm)
    {
        var reciprocal = new TrueHeading((runway.TrueHeading.Degrees + 180) % 360);
        (double Lat, double Lon) position = GeoMath.ProjectPoint(
            runway.ThresholdLatitude,
            runway.ThresholdLongitude,
            reciprocal,
            distFromThresholdNm
        );

        return new AircraftState
        {
            Callsign = "N157WT",
            AircraftType = "C172",
            Position = new LatLon(position.Lat, position.Lon),
            Altitude = runway.ElevationFt + altAgl,
            TrueHeading = runway.TrueHeading,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = runway },
        };
    }

    private static PhaseContext BuildCtx(AircraftState aircraft, RunwayInfo runway) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategorization.Categorize(aircraft.AircraftType),
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.ElevationFt,
            Logger = NullLogger.Instance,
        };
}
