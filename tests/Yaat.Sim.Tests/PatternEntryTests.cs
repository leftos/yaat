using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests that pattern entry commands (ERD, ERB, EF, etc.) build the correct
/// phase sequence including PatternEntryPhase for distant aircraft.
/// Uses OAK runway 28R geometry for realistic positions.
///
/// OAK 28R: heading ~292°, threshold at east end, departure end at west end.
/// Right pattern: crosswind heading 22° (NNE), pattern side is north/northeast.
/// Left pattern: crosswind heading 202° (SSW), pattern side is south/southwest.
///
/// Wrong-side detection uses AlongTrackDistanceNm along the crosswind heading.
/// The crosswind axis is perpendicular to the runway. Aircraft with negative
/// projection on this axis are on the wrong side and need a midfield crossing.
/// </summary>
public class PatternEntryTests
{
    private readonly ITestOutputHelper _output;

    public PatternEntryTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    // OAK runway 28R: heading ~292°, elevation 9ft
    // Threshold at east end, departure end at west end
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
            Latitude = lat,
            Longitude = lon,
            Altitude = alt,
            Heading = heading,
            IndicatedAirspeed = 120,
            GroundSpeed = 120,
            IsOnGround = false,
            Phases = new PhaseList(),
        };
    }

    /// <summary>Category as resolved by the handler (may differ from Piston if AircraftCategorization uninitialized).</summary>
    private static AircraftCategory ResolvedCategory => AircraftCategorization.Categorize("C182");

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
    // Basic downwind entry (from various directions)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ERD_FromNorth10nm_InsertsPatternEntryPhase()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.87, -122.22, 3500, 180); // 10nm north, heading south

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        var phases = aircraft.Phases.Phases;
        DumpPhases(aircraft);

        // First phase should be PatternEntryPhase (aircraft is far from pattern)
        Assert.IsType<PatternEntryPhase>(phases[0]);
        var entry = (PatternEntryPhase)phases[0];

        // Entry point should be near the downwind abeam point (midfield per AIM 4-3-3)
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right);
        double distToAbeam = GeoMath.DistanceNm(entry.EntryLat, entry.EntryLon, waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon);
        _output.WriteLine($"Entry point dist to DownwindAbeam: {distToAbeam:F3}nm");
        Assert.True(distToAbeam < 0.1, $"Entry point should be at midfield abeam. Dist: {distToAbeam:F3}nm");

        // Second phase should be DownwindPhase
        Assert.IsType<DownwindPhase>(phases[1]);

        // Should end with FinalApproachPhase + LandingPhase
        Assert.IsType<FinalApproachPhase>(phases[^2]);
    }

    [Fact]
    public void ERD_FromSouth5nm_WrongSide_MidfieldCrossing()
    {
        var runway = MakeOak28R();
        // 5nm south of OAK — for right pattern, pattern is to the north
        // South is the WRONG side
        var aircraft = MakeAircraft(37.65, -122.20, 2500, 360);

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
        Assert.IsType<DownwindPhase>(aircraft.Phases.Phases[1]);
        Assert.Contains("crossing midfield", result.Message);
    }

    [Fact]
    public void ERD_FromEast8nm_InsertsPatternEntryPhase()
    {
        var runway = MakeOak28R();
        // 8nm east of threshold (approach side), heading west
        // East projects positively on crosswind axis (22° NNE) → correct side for right pattern
        var aircraft = MakeAircraft(37.72, -122.07, 3000, 270);

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
        Assert.IsType<DownwindPhase>(aircraft.Phases.Phases[1]);
    }

    [Fact]
    public void ERD_FromWest6nm_WrongSide_MidfieldCrossing()
    {
        var runway = MakeOak28R();
        // 6nm west of airport — projects negatively on crosswind axis (22° NNE)
        // because west has large negative east-component. Wrong side for right pattern.
        var aircraft = MakeAircraft(37.73, -122.32, 2500, 90);

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        // West is wrong side for right pattern on 292° heading
        Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
    }

    [Fact]
    public void ERD_NearbyAircraft_NoPatternEntryPhase()
    {
        var runway = MakeOak28R();
        // Aircraft already near the downwind abeam point (midfield entry per AIM 4-3-3)
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right);
        var aircraft = MakeAircraft(waypoints.DownwindAbeamLat + 0.005, waypoints.DownwindAbeamLon, waypoints.PatternAltitude, 112);

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        // Should NOT have PatternEntryPhase since aircraft is already near the pattern
        Assert.IsType<DownwindPhase>(aircraft.Phases!.Phases[0]);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Left pattern downwind entries
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ELD_FromNorth8nm_LeftPattern_WrongSide_MidfieldCrossing()
    {
        var runway = MakeOak28R();
        // Left pattern: crosswind heading 202° (SSW), pattern side is south/southwest
        // North is WRONG side for left pattern
        var aircraft = MakeAircraft(37.85, -122.20, 3000, 180);

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
        Assert.Contains("crossing midfield", result.Message);
    }

    [Fact]
    public void ELD_FromSouth8nm_LeftPattern_CorrectSide()
    {
        var runway = MakeOak28R();
        // Left pattern: pattern is to the south → south is correct side
        var aircraft = MakeAircraft(37.60, -122.20, 3000, 360);

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
        Assert.IsType<DownwindPhase>(aircraft.Phases.Phases[1]);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Base leg entries (from various directions)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ERB_From8nmEast_InsertsPatternEntryPhase()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.80, -122.10, 3000, 270); // 8nm east, heading west

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Base, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
        Assert.IsType<BasePhase>(aircraft.Phases.Phases[1]);
    }

    [Fact]
    public void ERB_FromSouth_WrongSide_MidfieldCrossing()
    {
        var runway = MakeOak28R();
        // South is wrong side for right pattern base entry
        var aircraft = MakeAircraft(37.65, -122.20, 2500, 360);

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Base, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
    }

    [Fact]
    public void ERB_3nm_Final_EntryPointFurtherOut()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.80, -122.10, 3000, 270); // 8nm east

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

        Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);

        var entry = (PatternEntryPhase)aircraft.Phases.Phases[0];
        double distToThreshold = GeoMath.DistanceNm(entry.EntryLat, entry.EntryLon, runway.ThresholdLatitude, runway.ThresholdLongitude);
        _output.WriteLine($"Entry with 3nm final: dist to threshold: {distToThreshold:F2}nm");

        // Entry should be roughly 3nm from threshold (plus lateral offset)
        Assert.True(distToThreshold > 2.5, $"Entry should be >2.5nm from threshold with 3nm final, got {distToThreshold:F2}nm");
    }

    // ───────────────────────────────────────────────────────────────────────
    // Straight-in final entries
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void EF_From6nmOnFinal_HasPatternEntryPhase()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.68, -122.14, 2000, 292);

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Final, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases.Phases[1]);
    }

    [Fact]
    public void EF_FromNorth_StraightIn()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.85, -122.20, 3000, 180);

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Final, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases.Phases[1]);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Entry point geometry validation
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void DownwindEntryPoint_IsAtMidfieldAbeam_NotDepartureEnd()
    {
        var runway = MakeOak28R();
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right);

        var aircraft = MakeAircraft(37.87, -122.22, 3500, 180);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        var entry = (PatternEntryPhase)aircraft.Phases!.Phases[0];

        double distToAbeam = GeoMath.DistanceNm(entry.EntryLat, entry.EntryLon, waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon);
        double distToStart = GeoMath.DistanceNm(entry.EntryLat, entry.EntryLon, waypoints.DownwindStartLat, waypoints.DownwindStartLon);

        _output.WriteLine($"Entry dist to DownwindAbeam: {distToAbeam:F3}nm");
        _output.WriteLine($"Entry dist to DownwindStart: {distToStart:F3}nm");

        Assert.True(distToAbeam < 0.01, $"Entry should match DownwindAbeam exactly, dist: {distToAbeam:F4}nm");
        Assert.True(distToStart > distToAbeam, "Entry should be closer to DownwindAbeam than DownwindStart");
    }

    [Fact]
    public void PatternEntryPhase_SetsCorrectAltitude()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.87, -122.22, 3500, 180);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        var entry = (PatternEntryPhase)aircraft.Phases!.Phases[0];
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right);

        // Pattern altitude = field elevation + AGL for the resolved category
        double expectedAlt = 9 + CategoryPerformance.PatternAltitudeAgl(ResolvedCategory);
        Assert.Equal(expectedAlt, entry.PatternAltitude);
        Assert.Equal(waypoints.PatternAltitude, entry.PatternAltitude);
        _output.WriteLine(
            $"Pattern altitude: {entry.PatternAltitude:F0}ft (field {runway.ElevationFt:F0} + {CategoryPerformance.PatternAltitudeAgl(ResolvedCategory):F0} AGL)"
        );
    }

    // ───────────────────────────────────────────────────────────────────────
    // Comprehensive directional tests — 8 compass directions
    //
    // Right pattern on 292°: crosswind axis is 22° (NNE).
    // Positive projection = correct (pattern) side.
    // Aircraft far off on the west or south side project negatively.
    //
    // Left pattern on 292°: crosswind axis is 202° (SSW).
    // Positive projection = correct (pattern) side.
    // Aircraft far off on the north or east side project negatively.
    // ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("N", 37.87, -122.21, 180, false)] // North → NNE projection positive → correct
    [InlineData("NE", 37.83, -122.10, 225, false)] // NE → positive → correct
    [InlineData("E", 37.72, -122.07, 270, false)] // East → positive → correct
    [InlineData("SE", 37.65, -122.10, 315, true)] // SE → negative → wrong
    [InlineData("S", 37.63, -122.21, 0, true)] // South → negative → wrong
    [InlineData("SW", 37.65, -122.32, 45, true)] // SW → negative → wrong
    [InlineData("W", 37.73, -122.33, 90, true)] // West → negative → wrong
    [InlineData("NW", 37.82, -122.30, 135, false)] // NW → positive → correct
    public void ERD_RightPattern_FromAllDirections(string dir, double lat, double lon, double hdg, bool expectWrongSide)
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(lat, lon, 3000, hdg);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, $"[{dir}] Entry should succeed: {result.Message}");
        DumpPhases(aircraft);

        var phases = aircraft.Phases!.Phases;

        if (expectWrongSide)
        {
            Assert.IsType<MidfieldCrossingPhase>(phases[0]);
            _output.WriteLine($"[{dir}] Correctly detected wrong side → midfield crossing");
        }
        else
        {
            Assert.IsType<PatternEntryPhase>(phases[0]);
            _output.WriteLine($"[{dir}] Correctly detected correct side → pattern entry");
        }

        // All should end with approach and landing phases
        Assert.IsType<FinalApproachPhase>(phases[^2]);
    }

    [Theory]
    [InlineData("N", 37.87, -122.21, 180, true)] // North → SSW projection negative → wrong
    [InlineData("NE", 37.83, -122.10, 225, true)] // NE → negative → wrong
    [InlineData("E", 37.72, -122.07, 270, true)] // East → negative → wrong
    [InlineData("SE", 37.65, -122.10, 315, false)] // SE → positive → correct
    [InlineData("S", 37.63, -122.21, 0, false)] // South → positive → correct
    [InlineData("SW", 37.65, -122.32, 45, false)] // SW → positive → correct
    [InlineData("W", 37.73, -122.33, 90, false)] // West → positive → correct
    [InlineData("NW", 37.82, -122.30, 135, true)] // NW → negative → wrong
    public void ELD_LeftPattern_FromAllDirections(string dir, double lat, double lon, double hdg, bool expectWrongSide)
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(lat, lon, 3000, hdg);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, $"[{dir}] Entry should succeed: {result.Message}");
        DumpPhases(aircraft);

        var phases = aircraft.Phases!.Phases;

        if (expectWrongSide)
        {
            Assert.IsType<MidfieldCrossingPhase>(phases[0]);
            _output.WriteLine($"[{dir}] Correctly detected wrong side → midfield crossing");
        }
        else
        {
            Assert.IsType<PatternEntryPhase>(phases[0]);
            _output.WriteLine($"[{dir}] Correctly detected correct side → pattern entry");
        }

        Assert.IsType<FinalApproachPhase>(phases[^2]);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Phase sequence validation
    // ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PatternEntryLeg.Downwind, typeof(DownwindPhase))]
    [InlineData(PatternEntryLeg.Base, typeof(BasePhase))]
    [InlineData(PatternEntryLeg.Final, typeof(FinalApproachPhase))]
    public void EntryLeg_ProducesCorrectFirstPatternPhase(PatternEntryLeg leg, Type expectedPhase)
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.87, -122.22, 3500, 180);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, leg, runwayId: "28R");

        DumpPhases(aircraft);
        var phases = aircraft.Phases!.Phases;

        Assert.IsType<PatternEntryPhase>(phases[0]);

        if (leg == PatternEntryLeg.Final)
        {
            Assert.IsType<FinalApproachPhase>(phases[1]);
        }
        else
        {
            Assert.IsAssignableFrom(expectedPhase, phases[1]);
        }
    }

    [Fact]
    public void DownwindEntry_HasCorrectFullSequence()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.87, -122.22, 3500, 180);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        var phases = aircraft.Phases!.Phases;
        DumpPhases(aircraft);

        // Expected: PatternEntry → Downwind → Base → FinalApproach → Landing
        Assert.Equal(5, phases.Count);
        Assert.IsType<PatternEntryPhase>(phases[0]);
        Assert.IsType<DownwindPhase>(phases[1]);
        Assert.IsType<BasePhase>(phases[2]);
        Assert.IsType<FinalApproachPhase>(phases[3]);
        Assert.IsType<LandingPhase>(phases[4]);
    }

    [Fact]
    public void BaseEntry_HasCorrectSequence()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.87, -122.22, 3500, 180);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Base, runwayId: "28R");

        var phases = aircraft.Phases!.Phases;
        DumpPhases(aircraft);

        Assert.Equal(4, phases.Count);
        Assert.IsType<PatternEntryPhase>(phases[0]);
        Assert.IsType<BasePhase>(phases[1]);
        Assert.IsType<FinalApproachPhase>(phases[2]);
        Assert.IsType<LandingPhase>(phases[3]);
    }

    [Fact]
    public void FinalEntry_HasCorrectSequence()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.87, -122.22, 3500, 180);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Final, runwayId: "28R");

        var phases = aircraft.Phases!.Phases;
        DumpPhases(aircraft);

        Assert.Equal(3, phases.Count);
        Assert.IsType<PatternEntryPhase>(phases[0]);
        Assert.IsType<FinalApproachPhase>(phases[1]);
        Assert.IsType<LandingPhase>(phases[2]);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Midfield crossing validation
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void MidfieldCrossing_RightPattern_HasCorrectWaypoints()
    {
        var runway = MakeOak28R();
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right);

        // South of airport — wrong side for right pattern (pattern is north)
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        var mc = Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
        Assert.NotNull(mc.Waypoints);
        Assert.Equal(PatternDirection.Right, mc.Waypoints!.Direction);

        _output.WriteLine($"Pattern alt: {waypoints.PatternAltitude:F0}, crossing alt target: {waypoints.PatternAltitude + 500:F0}");
    }

    [Fact]
    public void MidfieldCrossing_LeftPattern_HasCorrectWaypoints()
    {
        var runway = MakeOak28R();

        // North of airport — wrong side for left pattern (pattern is south)
        var aircraft = MakeAircraft(37.87, -122.21, 3000, 180);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Downwind, runwayId: "28R");

        DumpPhases(aircraft);

        Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
        Assert.IsType<DownwindPhase>(aircraft.Phases.Phases[1]);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Pattern geometry validation
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void PatternGeometry_LeftPattern_TurnsAreLeft()
    {
        var runway = MakeOak28R();
        var wp = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Left);

        // For left pattern on runway heading 292:
        // Upwind = 292, Crosswind = 292 - 90 = 202, Downwind = 112, Base = 112 - 90 = 22
        Assert.Equal(292, wp.UpwindHeading);
        Assert.Equal(202, wp.CrosswindHeading);
        Assert.Equal(112, wp.DownwindHeading);
        Assert.Equal(22, wp.BaseHeading);
        Assert.Equal(292, wp.FinalHeading);
        Assert.Equal(PatternDirection.Left, wp.Direction);
    }

    [Fact]
    public void PatternGeometry_RightPattern_TurnsAreRight()
    {
        var runway = MakeOak28R();
        var wp = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Right);

        // For right pattern on runway heading 292:
        // Upwind = 292, Crosswind = 292 + 90 = 22, Downwind = 112, Base = 112 + 90 = 202
        Assert.Equal(292, wp.UpwindHeading);
        Assert.Equal(22, wp.CrosswindHeading);
        Assert.Equal(112, wp.DownwindHeading);
        Assert.Equal(202, wp.BaseHeading);
        Assert.Equal(292, wp.FinalHeading);
        Assert.Equal(PatternDirection.Right, wp.Direction);
    }

    [Fact]
    public void PatternGeometry_DownwindAbeam_IsAbeamThreshold()
    {
        var runway = MakeOak28R();
        var wp = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Right);

        double distToThreshold = GeoMath.DistanceNm(wp.DownwindAbeamLat, wp.DownwindAbeamLon, wp.ThresholdLat, wp.ThresholdLon);
        double expectedSize = CategoryPerformance.PatternSizeNm(AircraftCategory.Piston);
        _output.WriteLine($"DownwindAbeam dist to threshold: {distToThreshold:F3}nm, expected: {expectedSize:F3}nm");

        Assert.InRange(distToThreshold, expectedSize - 0.05, expectedSize + 0.05);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Closed traffic (takeoff into pattern)
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void CTO_MRT_ReplacesInitialClimbWithPatternPhases()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(runway.ThresholdLatitude, runway.ThresholdLongitude, 9, 292);
        aircraft.IsOnGround = true;
        aircraft.GroundSpeed = 0;
        aircraft.IndicatedAirspeed = 0;

        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new LinedUpAndWaitingPhase());
        phases.Add(new TakeoffPhase());
        phases.Add(new InitialClimbPhase());
        aircraft.Phases = phases;

        var cto = new ClearedForTakeoffCommand(new ClosedTrafficDeparture(PatternDirection.Right));

        var luaw = (LinedUpAndWaitingPhase)phases.Phases[0];
        var result = DepartureClearanceHandler.TryClearedForTakeoff(cto, aircraft, luaw, null);

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        var allPhases = aircraft.Phases.Phases;

        // InitialClimbPhase should have been removed for closed traffic
        Assert.DoesNotContain(allPhases, p => p is InitialClimbPhase);

        // Should have pattern phases
        Assert.Contains(allPhases, p => p is UpwindPhase);
        Assert.Contains(allPhases, p => p is CrosswindPhase);
        Assert.Contains(allPhases, p => p is DownwindPhase);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Edge cases
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ERD_NoRunway_Fails()
    {
        var aircraft = MakeAircraft(37.87, -122.22, 3500, 180);
        aircraft.Phases = new PhaseList();

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind);

        Assert.False(result.Success);
        Assert.Contains("runway", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ERD_OnGround_NoPatternEntryPhase()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.87, -122.22, 9, 180);
        aircraft.IsOnGround = true;
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        // On-ground aircraft should not get PatternEntryPhase
        // (but may get MidfieldCrossing if on wrong side — south for right pattern)
        // Place check on phase[0] type being either Downwind or MidfieldCrossing, not PatternEntry
        Assert.IsNotType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
    }

    [Fact]
    public void ERD_VeryClose_1nm_NoPatternEntryPhase()
    {
        var runway = MakeOak28R();
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right);

        // Place aircraft 0.8nm from the entry point (under 1nm threshold), on correct side
        // Move slightly toward the runway from the abeam point (stays on pattern side)
        double bearingToThreshold = GeoMath.BearingTo(
            waypoints.DownwindAbeamLat,
            waypoints.DownwindAbeamLon,
            runway.ThresholdLatitude,
            runway.ThresholdLongitude
        );
        var nearEntry = GeoMath.ProjectPoint(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon, bearingToThreshold, 0.3);
        var aircraft = MakeAircraft(nearEntry.Lat, nearEntry.Lon, waypoints.PatternAltitude, 112);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        // Should skip PatternEntryPhase when close to entry point
        Assert.IsType<DownwindPhase>(aircraft.Phases!.Phases[0]);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Different runway — SFO 28R (heading ~284°) for variety
    // ───────────────────────────────────────────────────────────────────────

    private static RunwayInfo MakeSfo28R()
    {
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KSFO",
            thresholdLat: 37.61348,
            thresholdLon: -122.35738,
            endLat: 37.62837,
            endLon: -122.39305,
            heading: 284,
            elevationFt: 13,
            lengthFt: 11870,
            widthFt: 200
        );
    }

    [Fact]
    public void SFO_ERD_FromSouth_RightPattern_WrongSide()
    {
        var runway = MakeSfo28R();
        // SFO 28R heading 284°, right pattern → pattern is to the north
        // South is wrong side for right pattern
        var aircraft = MakeAircraft(37.50, -122.37, 3000, 0);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
    }

    [Fact]
    public void SFO_ELD_FromSouth_LeftPattern_CorrectSide()
    {
        var runway = MakeSfo28R();
        // SFO 28R heading 284°, left pattern → pattern is to the south
        // South is correct side for left pattern
        var aircraft = MakeAircraft(37.50, -122.37, 3000, 0);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Downwind, runwayId: "28R");

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Different runway heading — runway 12 (heading ~120°) for variety
    // ───────────────────────────────────────────────────────────────────────

    private static RunwayInfo MakeRunway12()
    {
        return TestRunwayFactory.Make(
            designator: "12",
            airportId: "KTEST",
            thresholdLat: 37.75,
            thresholdLon: -122.25,
            endLat: 37.74,
            endLon: -122.235,
            heading: 120,
            elevationFt: 50,
            lengthFt: 5000,
            widthFt: 150
        );
    }

    [Theory]
    [InlineData("N", 37.85, -122.25, 180, false)] // North → left of 120° runway → left pattern side ✓
    [InlineData("S", 37.65, -122.25, 0, true)] // South → right of 120° runway → wrong side for left ✓
    [InlineData("E", 37.75, -122.15, 270, false)] // East → positive on 30° axis → correct side for left
    [InlineData("W", 37.75, -122.35, 90, true)] // West → negative on 30° axis → wrong side for left
    public void Runway12_LeftPattern_WrongSideDetection(string dir, double lat, double lon, double hdg, bool expectWrongSide)
    {
        var runway = MakeRunway12();
        var aircraft = MakeAircraft(lat, lon, 2500, hdg);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Downwind, runwayId: "12");

        Assert.True(result.Success, $"[{dir}] Entry should succeed: {result.Message}");
        DumpPhases(aircraft);

        if (expectWrongSide)
        {
            Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
        }
        else
        {
            Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
        }
    }
}
