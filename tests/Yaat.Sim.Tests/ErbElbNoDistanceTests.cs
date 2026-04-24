using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
/// <summary>
/// Tests for ERB/ELB without a distance argument: aircraft should turn
/// perpendicular to the extended centerline from its current position,
/// producing a base leg whose length equals the aircraft's current
/// cross-track distance. The derived FinalDistanceNm equals the aircraft's
/// along-track distance (outbound from threshold).
///
/// Uses OAK 28R (heading ~292°, threshold at east end). For right pattern:
/// - crosswind heading = 22° (NNE) → pattern side is NNE of runway
/// - base heading = 202° (SSW, turning right onto final)
/// For left pattern:
/// - crosswind heading = 202° (SSW) → pattern side is SSW of runway
/// - base heading = 22° (NNE, turning left onto final)
/// </summary>
public class ErbElbNoDistanceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navDbScope;

    public ErbElbNoDistanceTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(MakeOak28R()));
    }

    public void Dispose() => _navDbScope.Dispose();

    private static RunwayInfo MakeOak28R()
    {
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK",
            thresholdLat: 37.72152,
            thresholdLon: -122.20065,
            endLat: 37.73089,
            endLon: -122.21926,
            heading: 292,
            elevationFt: 9,
            lengthFt: 6213,
            widthFt: 150
        );
    }

    private static AircraftState MakeAircraft(double lat, double lon, double alt, double heading)
    {
        return new AircraftState
        {
            Callsign = "N775JW",
            AircraftType = "C182",
            Position = new LatLon(lat, lon),
            Altitude = alt,
            TrueHeading = new TrueHeading(heading),
            IndicatedAirspeed = 120,
            IsOnGround = false,
            Phases = new PhaseList(),
        };
    }

    /// <summary>
    /// Build an aircraft position relative to the runway threshold using runway-relative coordinates:
    /// <paramref name="alongTrackOutboundNm"/> is NM outbound along the reciprocal of runway heading
    /// (i.e., along the approach direction away from the threshold).
    /// <paramref name="crossTrackRightNm"/> is NM perpendicular to centerline (positive = to the right
    /// of the landing direction, i.e., NNE for runway 28).
    /// </summary>
    private static (double Lat, double Lon) PositionFromThreshold(RunwayInfo runway, double alongTrackOutboundNm, double crossTrackRightNm)
    {
        var reciprocal = new TrueHeading((runway.TrueHeading.Degrees + 180) % 360);
        var centerline = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, alongTrackOutboundNm);

        // crossTrackRightNm positive = project at (runwayHeading + 90°) from the centerline point
        // (right of the landing direction = NNE for runway 28).
        double crossHdg = crossTrackRightNm >= 0 ? (runway.TrueHeading.Degrees + 90) % 360 : (runway.TrueHeading.Degrees + 270) % 360;
        var result = GeoMath.ProjectPoint(centerline.Lat, centerline.Lon, new TrueHeading(crossHdg), Math.Abs(crossTrackRightNm));
        return (result.Lat, result.Lon);
    }

    private void DumpPhases(AircraftState aircraft)
    {
        var phases = aircraft.Phases!.Phases;
        _output.WriteLine($"Phases ({phases.Count}):");
        foreach (var p in phases)
        {
            _output.WriteLine($"  {p.GetType().Name}: {p.Name}");
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    // Happy path: perpendicular base from present position
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ERB_NoDistance_AlongTrack2nm_CrossTrack3nmNNE_SkipsPatternEntryAndSetsDerivedFinalDistance()
    {
        var runway = MakeOak28R();

        // Right pattern on 28R: NNE side is correct side.
        // Position aircraft 2nm outbound + 3nm NNE of centerline.
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 2.0, crossTrackRightNm: 3.0);
        var aircraft = MakeAircraft(lat, lon, 2000, heading: 90);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        // First phase should be BasePhase directly — PatternEntryPhase skipped
        // because the entry point equals aircraft's current position.
        Assert.IsType<BasePhase>(aircraft.Phases!.Phases[0]);

        var basePhase = (BasePhase)aircraft.Phases.Phases[0];
        Assert.NotNull(basePhase.FinalDistanceNm);

        // Derived FinalDistanceNm should equal aircraft's along-track distance (~2nm)
        _output.WriteLine($"BasePhase.FinalDistanceNm = {basePhase.FinalDistanceNm:F2}");
        Assert.InRange(basePhase.FinalDistanceNm!.Value, 1.8, 2.2);
    }

    [Fact]
    public void ERB_NoDistance_PistonAt1_5nmAlong_BasePhaseFinalDistanceApprox1_5nm()
    {
        var runway = MakeOak28R();
        // C182 is a piston (minimum 1.0nm floor). 1.5nm along-track clears the floor.
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 1.5, crossTrackRightNm: 2.0);
        var aircraft = MakeAircraft(lat, lon, 1000, heading: 270);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<BasePhase>(aircraft.Phases!.Phases[0]);
        var basePhase = (BasePhase)aircraft.Phases.Phases[0];
        Assert.NotNull(basePhase.FinalDistanceNm);
        Assert.InRange(basePhase.FinalDistanceNm!.Value, 1.3, 1.7);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Left pattern mirror
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ELB_NoDistance_AircraftOnLeftPatternSide_SkipsPatternEntryAndSetsDerivedFinalDistance()
    {
        var runway = MakeOak28R();

        // Left pattern on 28R: SSW side is correct side (crossTrackRightNm negative).
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 2.0, crossTrackRightNm: -3.0);
        var aircraft = MakeAircraft(lat, lon, 2000, heading: 90);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Left,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<BasePhase>(aircraft.Phases!.Phases[0]);
        var basePhase = (BasePhase)aircraft.Phases.Phases[0];
        Assert.NotNull(basePhase.FinalDistanceNm);
        Assert.InRange(basePhase.FinalDistanceNm!.Value, 1.8, 2.2);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Regression: with-distance path unchanged
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ERB_WithDistance_RegressionPreserved_PatternEntryPhaseInserted()
    {
        var runway = MakeOak28R();

        // 2nm outbound + 5nm NNE — large enough to trigger distToEntry > 1nm
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 2.0, crossTrackRightNm: 5.0);
        var aircraft = MakeAircraft(lat, lon, 3000, heading: 270);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: 3.0
        );

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        // With explicit distance, existing path: PatternEntryPhase → BasePhase(FinalDistanceNm=3.0)
        Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
        Assert.IsType<BasePhase>(aircraft.Phases.Phases[1]);

        var basePhase = (BasePhase)aircraft.Phases.Phases[1];
        Assert.Equal(3.0, basePhase.FinalDistanceNm);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Past-threshold rejection
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ERB_NoDistance_AircraftPastThreshold_RejectsAsTooClose()
    {
        var runway = MakeOak28R();

        // Negative along-track = aircraft is on the landing side (past threshold).
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: -1.0, crossTrackRightNm: 2.0);
        var aircraft = MakeAircraft(lat, lon, 1000, heading: 90);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.False(result.Success);
        _output.WriteLine($"Rejection message: {result.Message}");
        Assert.Contains("too close for base", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ERB_NoDistance_PistonAt0_8nmAlong_RejectsBelowPistonFloor()
    {
        var runway = MakeOak28R();

        // 0.8nm outbound — below the 1.0nm piston floor (C182).
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 0.8, crossTrackRightNm: 2.0);
        var aircraft = MakeAircraft(lat, lon, 1000, heading: 90);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.False(result.Success);
        Assert.Contains("too close for base", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ERB_NoDistance_JetAt1_5nmAlong_RejectsBelowJetFloor()
    {
        var runway = MakeOak28R();

        // 1.5nm outbound — above piston floor (1.0nm) but below jet floor (2.0nm).
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 1.5, crossTrackRightNm: 3.0);

        // Force jet category by using a known jet ICAO type (B738 — 737-800).
        var aircraft = new AircraftState
        {
            Callsign = "DAL123",
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            Altitude = 2500,
            TrueHeading = new TrueHeading(90),
            IndicatedAirspeed = 200,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = runway },
        };

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.False(result.Success);
        _output.WriteLine($"Jet rejection message: {result.Message}");
        Assert.Contains("too close for base", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ERB_NoDistance_AircraftTooHighForDescent_RejectsAsTooHigh()
    {
        var runway = MakeOak28R();

        // Short path (2nm along + 2nm cross = 4nm). At piston base speed 80 kt,
        // that's 3 min; at 700 fpm descent, max descent = 2100 ft. Aircraft at
        // 5000 ft (4991 ft AGL from field elev 9) exceeds that.
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 2.0, crossTrackRightNm: 2.0);
        var aircraft = MakeAircraft(lat, lon, 5000, heading: 270);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.False(result.Success);
        _output.WriteLine($"Too-high rejection message: {result.Message}");
        Assert.Contains("too high for base", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ERB_NoDistance_AircraftAtTpa_AcceptsDescentFeasible()
    {
        var runway = MakeOak28R();

        // Aircraft near TPA — altitude feasibility passes easily.
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 2.0, crossTrackRightNm: 2.0);
        var aircraft = MakeAircraft(lat, lon, 1000, heading: 270);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.True(result.Success, result.Message);
        Assert.IsType<BasePhase>(aircraft.Phases!.Phases[0]);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Wrong-side preservation
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ERB_NoDistance_WrongSide_StillGoesThroughMidfieldCrossing()
    {
        var runway = MakeOak28R();

        // Right pattern wants NNE side; SSW is wrong side.
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 2.0, crossTrackRightNm: -3.0);
        var aircraft = MakeAircraft(lat, lon, 2500, heading: 90);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        // Wrong-side path: MidfieldCrossingPhase first, then downwind + base;
        // BasePhase.FinalDistanceNm must remain null (derivation must not fire).
        Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);

        var basePhaseInCircuit = aircraft.Phases.Phases.OfType<BasePhase>().FirstOrDefault();
        Assert.NotNull(basePhaseInCircuit);
        Assert.Null(basePhaseInCircuit!.FinalDistanceNm);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Sanity check: base heading matches expected pattern geometry
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ERB_NoDistance_BasePhaseWaypointsHaveRightBaseHeading()
    {
        var runway = MakeOak28R();
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 2.0, crossTrackRightNm: 3.0);
        var aircraft = MakeAircraft(lat, lon, 2000, heading: 90);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Base, runwayId: "28R", finalDistanceNm: null);

        var basePhase = (BasePhase)aircraft.Phases!.Phases[0];
        Assert.NotNull(basePhase.Waypoints);

        // Right base for rwy 292° → base heading 292 + 90 - 180 = 202°
        double baseHdg = basePhase.Waypoints!.BaseHeading.Degrees;
        _output.WriteLine($"BaseHeading={baseHdg:F1}° (expected ~202°)");
        Assert.InRange(baseHdg, 200.0, 204.0);
    }

    [Fact]
    public void ELB_NoDistance_BasePhaseWaypointsHaveLeftBaseHeading()
    {
        var runway = MakeOak28R();
        var (lat, lon) = PositionFromThreshold(runway, alongTrackOutboundNm: 2.0, crossTrackRightNm: -3.0);
        var aircraft = MakeAircraft(lat, lon, 2000, heading: 90);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Base, runwayId: "28R", finalDistanceNm: null);

        var basePhase = (BasePhase)aircraft.Phases!.Phases[0];
        Assert.NotNull(basePhase.Waypoints);

        // Left base for rwy 292° → base heading 292 - 90 + 180 = 22° (wraps via reciprocal + (-90))
        double baseHdg = basePhase.Waypoints!.BaseHeading.Degrees;
        _output.WriteLine($"BaseHeading={baseHdg:F1}° (expected ~22°)");
        Assert.InRange(baseHdg, 20.0, 24.0);
    }
}
