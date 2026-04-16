using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
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
public class PatternEntryTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IDisposable _navDbScope;

    public PatternEntryTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(MakeOak28R(), MakeSfo28R(), MakeRunway12()));
    }

    public void Dispose() => _navDbScope.Dispose();

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
            TrueHeading = new TrueHeading(heading),
            IndicatedAirspeed = 120,
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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        var phases = aircraft.Phases.Phases;
        DumpPhases(aircraft);

        // First phase should be PatternEntryPhase (aircraft is far from pattern)
        Assert.IsType<PatternEntryPhase>(phases[0]);
        var entry = (PatternEntryPhase)phases[0];

        // Entry point should be near the downwind abeam point (midfield per AIM 4-3-3)
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right, null, null, null);
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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right, null, null, null);
        var aircraft = MakeAircraft(waypoints.DownwindAbeamLat + 0.005, waypoints.DownwindAbeamLon, waypoints.PatternAltitude, 112);

        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Base,
            runwayId: "28R",
            finalDistanceNm: null
        );

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Left,
            PatternEntryLeg.Final,
            runwayId: "28R",
            finalDistanceNm: null
        );

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Left,
            PatternEntryLeg.Final,
            runwayId: "28R",
            finalDistanceNm: null
        );

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
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right, null, null, null);

        var aircraft = MakeAircraft(37.87, -122.22, 3500, 180);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

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

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var entry = (PatternEntryPhase)aircraft.Phases!.Phases[0];
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right, null, null, null);

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, leg, runwayId: "28R", finalDistanceNm: null);

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

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

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

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Base, runwayId: "28R", finalDistanceNm: null);

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

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Final, runwayId: "28R", finalDistanceNm: null);

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
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right, null, null, null);

        // South of airport — wrong side for right pattern (pattern is north)
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

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

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

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
        var wp = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Left, null, null, null);

        // For left pattern on runway heading 292:
        // Upwind = 292, Crosswind = 292 - 90 = 202, Downwind = 112, Base = 112 - 90 = 22
        Assert.Equal(292, wp.UpwindHeading.Degrees);
        Assert.Equal(202, wp.CrosswindHeading.Degrees);
        Assert.Equal(112, wp.DownwindHeading.Degrees);
        Assert.Equal(22, wp.BaseHeading.Degrees);
        Assert.Equal(292, wp.FinalHeading.Degrees);
        Assert.Equal(PatternDirection.Left, wp.Direction);
    }

    [Fact]
    public void PatternGeometry_RightPattern_TurnsAreRight()
    {
        var runway = MakeOak28R();
        var wp = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Right, null, null, null);

        // For right pattern on runway heading 292:
        // Upwind = 292, Crosswind = 292 + 90 = 22, Downwind = 112, Base = 112 + 90 = 202
        Assert.Equal(292, wp.UpwindHeading.Degrees);
        Assert.Equal(22, wp.CrosswindHeading.Degrees);
        Assert.Equal(112, wp.DownwindHeading.Degrees);
        Assert.Equal(202, wp.BaseHeading.Degrees);
        Assert.Equal(292, wp.FinalHeading.Degrees);
        Assert.Equal(PatternDirection.Right, wp.Direction);
    }

    [Fact]
    public void PatternGeometry_DownwindAbeam_IsAbeamThreshold()
    {
        var runway = MakeOak28R();
        var wp = PatternGeometry.Compute(runway, AircraftCategory.Piston, PatternDirection.Right, null, null, null);

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
        aircraft.IndicatedAirspeed = 0;

        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new LinedUpAndWaitingPhase());
        phases.Add(new TakeoffPhase());
        phases.Add(new InitialClimbPhase());
        aircraft.Phases = phases;

        var cto = new ClearedForTakeoffCommand(new ClosedTrafficDeparture(PatternDirection.Right, null, null));

        var luaw = (LinedUpAndWaitingPhase)phases.Phases[0];
        var result = DepartureClearanceHandler.TryClearedForTakeoff(cto, aircraft, luaw);

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

        var result = PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, null, null);

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

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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
        var waypoints = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right, null, null, null);

        // Place aircraft 0.8nm from the entry point (under 1nm threshold), on correct side
        // Move slightly toward the runway from the abeam point (stays on pattern side)
        double bearingToThreshold = GeoMath.BearingTo(
            waypoints.DownwindAbeamLat,
            waypoints.DownwindAbeamLon,
            runway.ThresholdLatitude,
            runway.ThresholdLongitude
        );
        var nearEntry = GeoMath.ProjectPoint(waypoints.DownwindAbeamLat, waypoints.DownwindAbeamLon, new TrueHeading(bearingToThreshold), 0.3);
        var aircraft = MakeAircraft(nearEntry.Lat, nearEntry.Lon, waypoints.PatternAltitude, 112);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));
        // SFO 28R heading 284°, right pattern → pattern is to the north
        // South is wrong side for right pattern
        var aircraft = MakeAircraft(37.50, -122.37, 3000, 0);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
    }

    [Fact]
    public void SFO_ELD_FromSouth_LeftPattern_CorrectSide()
    {
        var runway = MakeSfo28R();
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));
        // SFO 28R heading 284°, left pattern → pattern is to the south
        // South is correct side for left pattern
        var aircraft = MakeAircraft(37.50, -122.37, 3000, 0);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

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
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));
        var aircraft = MakeAircraft(lat, lon, 2500, hdg);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            runwayId: "12",
            finalDistanceNm: null
        );

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

    // ───────────────────────────────────────────────────────────────────────
    // 45° midfield entry selection (AIM 4-3-3)
    //
    // ERD/ELD should prefer a 45° intercept lead-in (from the outside of the
    // pattern, at 45° to the downwind leg) when that requires less maneuvering
    // than a straight-in join to the extended downwind leg. The extended-downwind
    // lead-in is preserved for upwind-aligned aircraft.
    //
    // Right pattern:  entry heading = downwind + 45°, lead-in bearing from abeam = (entry + 180°)
    // Left pattern:   entry heading = downwind - 45°, lead-in bearing from abeam = (entry + 180°)
    // Lead-in distance for 45° entry = 50% of runway length.
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ERD_AircraftDownwindOfAbeam_Picks45DegreeLeadIn()
    {
        // VPMOR-like: aircraft east of KOAK heading west. The extended-downwind
        // lead-in would be west of abeam (reverse of downwind heading 112°, i.e. 292°),
        // forcing a near-U-turn at the lead-in and flying past the field.
        // 45° entry lead-in is NNW of abeam (reverse of entry heading 157°, i.e. 337°).
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.72, -122.00, 3000, 270); // ~10nm east, heading west
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        var entry = Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
        Assert.NotNull(entry.LeadInLat);
        Assert.NotNull(entry.LeadInLon);

        var wp = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right, null, null, null);
        double bearingAbeamToLeadIn = GeoMath.BearingTo(wp.DownwindAbeamLat, wp.DownwindAbeamLon, entry.LeadInLat!.Value, entry.LeadInLon!.Value);
        double expected45Bearing = new TrueHeading(wp.DownwindHeading.Degrees + 45.0 + 180.0).Degrees; // 337° for OAK 28R right
        double expectedXDWBearing = wp.DownwindHeading.ToReciprocal().Degrees; // 292° for OAK 28R

        _output.WriteLine(
            $"Lead-in bearing from abeam: {bearingAbeamToLeadIn:F1}° (45° expects {expected45Bearing:F1}°, XDW expects {expectedXDWBearing:F1}°)"
        );

        double distTo45 = GeoMath.AbsBearingDifference(bearingAbeamToLeadIn, expected45Bearing);
        double distToXDW = GeoMath.AbsBearingDifference(bearingAbeamToLeadIn, expectedXDWBearing);
        Assert.True(
            distTo45 < distToXDW,
            $"Lead-in should be on 45° side of abeam. 45° side ({expected45Bearing:F0}°) off by {distTo45:F1}°; XDW side ({expectedXDWBearing:F0}°) off by {distToXDW:F1}°."
        );
    }

    [Fact]
    public void ELD_AircraftDownwindOfAbeam_Picks45DegreeLeadIn()
    {
        // Left pattern mirror: aircraft southeast of KOAK heading west.
        // Left pattern downwind is south of runway. Extended-downwind lead-in
        // would force a near-U-turn. 45° entry lead-in is SW of abeam
        // (reverse of entry heading 67°, i.e. 247°).
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.65, -122.00, 3000, 270); // SE of field, heading west, correct side for left pattern
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        var entry = Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
        Assert.NotNull(entry.LeadInLat);
        Assert.NotNull(entry.LeadInLon);

        var wp = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Left, null, null, null);
        double bearingAbeamToLeadIn = GeoMath.BearingTo(wp.DownwindAbeamLat, wp.DownwindAbeamLon, entry.LeadInLat!.Value, entry.LeadInLon!.Value);
        double expected45Bearing = new TrueHeading(wp.DownwindHeading.Degrees - 45.0 + 180.0).Degrees; // 247° for OAK 28R left
        double expectedXDWBearing = wp.DownwindHeading.ToReciprocal().Degrees;

        _output.WriteLine(
            $"Lead-in bearing from abeam: {bearingAbeamToLeadIn:F1}° (45° expects {expected45Bearing:F1}°, XDW expects {expectedXDWBearing:F1}°)"
        );

        double distTo45 = GeoMath.AbsBearingDifference(bearingAbeamToLeadIn, expected45Bearing);
        double distToXDW = GeoMath.AbsBearingDifference(bearingAbeamToLeadIn, expectedXDWBearing);
        Assert.True(
            distTo45 < distToXDW,
            $"Lead-in should be on 45° side of abeam. 45° side ({expected45Bearing:F0}°) off by {distTo45:F1}°; XDW side ({expectedXDWBearing:F0}°) off by {distToXDW:F1}°."
        );
    }

    [Fact]
    public void ERD_AircraftUpwindAligned_PicksExtendedDownwindLeadIn()
    {
        // Aircraft positioned ~5nm upwind of abeam, on the extended downwind leg,
        // heading downwind direction. Extended-downwind entry is a pure straight-in
        // (0° turns everywhere) — should be preferred over 45°.
        var runway = MakeOak28R();
        var wp = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right, null, null, null);
        var upwindPos = GeoMath.ProjectPoint(wp.DownwindAbeamLat, wp.DownwindAbeamLon, wp.DownwindHeading.ToReciprocal(), 5.0);
        var aircraft = MakeAircraft(upwindPos.Lat, upwindPos.Lon, wp.PatternAltitude, wp.DownwindHeading.Degrees);
        aircraft.Phases!.AssignedRunway = runway;

        var result = PatternCommandHandler.TryEnterPattern(
            aircraft,
            PatternDirection.Right,
            PatternEntryLeg.Downwind,
            runwayId: "28R",
            finalDistanceNm: null
        );

        Assert.True(result.Success, result.Message);
        DumpPhases(aircraft);

        var entry = Assert.IsType<PatternEntryPhase>(aircraft.Phases!.Phases[0]);
        Assert.NotNull(entry.LeadInLat);
        Assert.NotNull(entry.LeadInLon);

        double bearingAbeamToLeadIn = GeoMath.BearingTo(wp.DownwindAbeamLat, wp.DownwindAbeamLon, entry.LeadInLat!.Value, entry.LeadInLon!.Value);
        double expected45Bearing = new TrueHeading(wp.DownwindHeading.Degrees + 45.0 + 180.0).Degrees;
        double expectedXDWBearing = wp.DownwindHeading.ToReciprocal().Degrees;

        _output.WriteLine(
            $"Lead-in bearing from abeam: {bearingAbeamToLeadIn:F1}° (45° expects {expected45Bearing:F1}°, XDW expects {expectedXDWBearing:F1}°)"
        );

        double distTo45 = GeoMath.AbsBearingDifference(bearingAbeamToLeadIn, expected45Bearing);
        double distToXDW = GeoMath.AbsBearingDifference(bearingAbeamToLeadIn, expectedXDWBearing);
        Assert.True(
            distToXDW < distTo45,
            $"Upwind-aligned aircraft should pick extended-downwind lead-in. XDW side ({expectedXDWBearing:F0}°) off by {distToXDW:F1}°; 45° side ({expected45Bearing:F0}°) off by {distTo45:F1}°."
        );
    }

    [Fact]
    public void Erd_Piston_LeadInDistanceIs50PercentOfRunwayLength()
    {
        // Pistons get no floor — 0.5 × runway length is the stabilization distance.
        // For OAK 28R (6213ft) with a C182 (piston), that is ~0.511nm.
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.72, -122.00, 3000, 270); // C182, VPMOR-like → 45° expected
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var entry = (PatternEntryPhase)aircraft.Phases!.Phases[0];
        var wp = PatternGeometry.Compute(runway, ResolvedCategory, PatternDirection.Right, null, null, null);
        double distAbeamToLeadIn = GeoMath.DistanceNm(wp.DownwindAbeamLat, wp.DownwindAbeamLon, entry.LeadInLat!.Value, entry.LeadInLon!.Value);
        double expectedNm = runway.LengthFt * 0.5 / 6076.12;

        _output.WriteLine($"Piston lead-in distance from abeam: {distAbeamToLeadIn:F3}nm (expected {expectedNm:F3}nm for 6213ft runway)");
        Assert.InRange(distAbeamToLeadIn, expectedNm - 0.03, expectedNm + 0.03);
    }

    [Fact]
    public void Erd_Turboprop_LeadInDistanceHas1_5nmFloor()
    {
        // Turboprops get a 1.5nm floor. For OAK 28R (6213ft), 0.5×length=0.511nm, so
        // the floor dominates: expect 1.5nm.
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.72, -122.00, 3000, 270);
        aircraft.AircraftType = "DH8D"; // Dash 8-Q400 — turboprop
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var entry = (PatternEntryPhase)aircraft.Phases!.Phases[0];
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        Assert.Equal(AircraftCategory.Turboprop, cat);
        var wp = PatternGeometry.Compute(runway, cat, PatternDirection.Right, null, null, null);
        double distAbeamToLeadIn = GeoMath.DistanceNm(wp.DownwindAbeamLat, wp.DownwindAbeamLon, entry.LeadInLat!.Value, entry.LeadInLon!.Value);

        _output.WriteLine($"Turboprop lead-in distance from abeam: {distAbeamToLeadIn:F3}nm (expected 1.5nm floor)");
        Assert.InRange(distAbeamToLeadIn, 1.47, 1.53);
    }

    [Fact]
    public void Erd_Jet_LeadInDistanceHas2_0nmFloor()
    {
        // Jets get a 2.0nm floor. For OAK 28R (6213ft), expect 2.0nm.
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.72, -122.00, 3000, 270);
        aircraft.AircraftType = "B738"; // 737-800 — jet
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var entry = (PatternEntryPhase)aircraft.Phases!.Phases[0];
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        Assert.Equal(AircraftCategory.Jet, cat);
        var wp = PatternGeometry.Compute(runway, cat, PatternDirection.Right, null, null, null);
        double distAbeamToLeadIn = GeoMath.DistanceNm(wp.DownwindAbeamLat, wp.DownwindAbeamLon, entry.LeadInLat!.Value, entry.LeadInLon!.Value);

        _output.WriteLine($"Jet lead-in distance from abeam: {distAbeamToLeadIn:F3}nm (expected 2.0nm floor)");
        Assert.InRange(distAbeamToLeadIn, 1.97, 2.03);
    }

    [Fact]
    public void Erd_Jet_OnVeryLongRunway_LeadInDistanceIs50PercentOfLength()
    {
        // For a >4nm runway, 0.5×length > 2.0nm, so the floor stops binding.
        // SFO 28R is 11870ft = 1.95nm half-length, which is still under 2.0nm, so
        // the jet floor barely dominates. Use a synthesized runway-length test:
        // we pick a hypothetical 30000ft runway → half=2.47nm > 2.0 floor.
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "KOAK", // reuse KOAK so NavDb finds the 28R designator
            thresholdLat: 37.72152,
            thresholdLon: -122.20065,
            endLat: 37.73833,
            endLon: -122.23416, // ~30000ft = 4.94nm, heading ~292°
            heading: 292,
            elevationFt: 9,
            lengthFt: 30000,
            widthFt: 150
        );
        using var _ = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(runway));

        var aircraft = MakeAircraft(37.72, -122.00, 3000, 270);
        aircraft.AircraftType = "B738";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var entry = (PatternEntryPhase)aircraft.Phases!.Phases[0];
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        var wp = PatternGeometry.Compute(runway, cat, PatternDirection.Right, null, null, null);
        double distAbeamToLeadIn = GeoMath.DistanceNm(wp.DownwindAbeamLat, wp.DownwindAbeamLon, entry.LeadInLat!.Value, entry.LeadInLon!.Value);
        double expectedNm = runway.LengthFt * 0.5 / 6076.12;

        _output.WriteLine($"Jet on long runway lead-in distance: {distAbeamToLeadIn:F3}nm (expected {expectedNm:F3}nm, floor {2.0:F1}nm)");
        Assert.InRange(distAbeamToLeadIn, expectedNm - 0.05, expectedNm + 0.05);
    }

    // ───────────────────────────────────────────────────────────────────────
    // Wrong-side teardrop re-entry (AIM 4-3-3.1.b, AC 90-66B §11.3-§11.4)
    //
    // Pistons and helicopters cross midfield at pattern altitude and drop
    // directly into DownwindPhase. Turboprops and jets cross at TPA+500 and
    // hand off to TeardropReentryPhase, which descends to TPA via an outbound
    // leg and 45° intercept to abeam.
    // ───────────────────────────────────────────────────────────────────────

    private static PhaseContext MakeContext(AircraftState aircraft) => CommandDispatcher.BuildMinimalContext(aircraft);

    [Fact]
    public void WrongSide_Piston_CrossesAtTpa()
    {
        // C182 south of KOAK 28R, right pattern → south is wrong side.
        // Piston should cross AT pattern altitude (not +500).
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0); // C182 default
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var mc = Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
        var ctx = MakeContext(aircraft);
        mc.OnStart(ctx);

        double expectedAlt = mc.Waypoints!.PatternAltitude;
        _output.WriteLine($"Piston crossing target alt: {ctx.Targets.TargetAltitude:F0}ft (expected {expectedAlt:F0}ft)");
        Assert.Equal(expectedAlt, ctx.Targets.TargetAltitude);
    }

    [Fact]
    public void WrongSide_Jet_CrossesAtTpaPlus500()
    {
        // B738 same position. Jet crosses at TPA+500.
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.AircraftType = "B738";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var mc = Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
        var ctx = MakeContext(aircraft);
        mc.OnStart(ctx);

        double expectedAlt = mc.Waypoints!.PatternAltitude + 500.0;
        _output.WriteLine($"Jet crossing target alt: {ctx.Targets.TargetAltitude:F0}ft (expected {expectedAlt:F0}ft)");
        Assert.Equal(expectedAlt, ctx.Targets.TargetAltitude);
    }

    [Fact]
    public void WrongSide_Turboprop_PhaseChain_IncludesTeardrop()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.AircraftType = "DH8D";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var phases = aircraft.Phases!.Phases;
        DumpPhases(aircraft);
        Assert.IsType<MidfieldCrossingPhase>(phases[0]);
        Assert.IsType<TeardropReentryPhase>(phases[1]);
        Assert.IsType<DownwindPhase>(phases[2]);
    }

    [Fact]
    public void WrongSide_Jet_PhaseChain_IncludesTeardrop()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.AircraftType = "B738";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var phases = aircraft.Phases!.Phases;
        DumpPhases(aircraft);
        Assert.IsType<MidfieldCrossingPhase>(phases[0]);
        Assert.IsType<TeardropReentryPhase>(phases[1]);
        Assert.IsType<DownwindPhase>(phases[2]);
    }

    [Fact]
    public void WrongSide_Piston_PhaseChain_NoTeardrop()
    {
        // C182 wrong-side: only MidfieldCrossing → Downwind, no teardrop.
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var phases = aircraft.Phases!.Phases;
        DumpPhases(aircraft);
        Assert.IsType<MidfieldCrossingPhase>(phases[0]);
        Assert.IsType<DownwindPhase>(phases[1]);
        Assert.DoesNotContain(phases, p => p is TeardropReentryPhase);
    }

    [Fact]
    public void TeardropReentry_RightPattern_OutboundAnchorOnPatternSide()
    {
        // Outbound anchor should be on crosswind heading from abeam (pattern-side perpendicular).
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.AircraftType = "B738";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);
        var teardrop = (TeardropReentryPhase)aircraft.Phases!.Phases[1];
        var ctx = MakeContext(aircraft);
        teardrop.OnStart(ctx);

        var route = ctx.Targets.NavigationRoute;
        Assert.True(route.Count >= 1, "Teardrop route should have at least one waypoint");
        var outbound = route[0];
        double bearingAbeamToOutbound = GeoMath.BearingTo(
            teardrop.Waypoints.DownwindAbeamLat,
            teardrop.Waypoints.DownwindAbeamLon,
            outbound.Latitude,
            outbound.Longitude
        );
        double expected = teardrop.Waypoints.CrosswindHeading.Degrees; // 22° for right pattern 28R

        _output.WriteLine($"Outbound anchor bearing from abeam: {bearingAbeamToOutbound:F1}° (expected ~{expected:F1}°)");
        Assert.InRange(GeoMath.AbsBearingDifference(bearingAbeamToOutbound, expected), 0, 5.0);
    }

    [Fact]
    public void TeardropReentry_LeftPattern_OutboundAnchorOnPatternSide()
    {
        // Mirror for left pattern — outbound on crosswind heading 202° for 28L.
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.87, -122.21, 3000, 180); // N of field, wrong side for left pattern
        aircraft.AircraftType = "B738";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Left, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);
        var teardrop = (TeardropReentryPhase)aircraft.Phases!.Phases[1];
        var ctx = MakeContext(aircraft);
        teardrop.OnStart(ctx);

        var outbound = ctx.Targets.NavigationRoute[0];
        double bearing = GeoMath.BearingTo(
            teardrop.Waypoints.DownwindAbeamLat,
            teardrop.Waypoints.DownwindAbeamLon,
            outbound.Latitude,
            outbound.Longitude
        );
        double expected = teardrop.Waypoints.CrosswindHeading.Degrees; // 202° for left pattern

        _output.WriteLine($"Outbound anchor bearing from abeam: {bearing:F1}° (expected ~{expected:F1}°)");
        Assert.InRange(GeoMath.AbsBearingDifference(bearing, expected), 0, 5.0);
    }

    [Fact]
    public void TeardropReentry_LeadInAndAbeamAreOn45DegreeLine()
    {
        // Right pattern: lead-in and abeam (waypoints 2 and 3 in the route) should both be
        // on the 45° reverse-entry line (bearing 337° from abeam).
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.AircraftType = "B738";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);
        var teardrop = (TeardropReentryPhase)aircraft.Phases!.Phases[1];
        var ctx = MakeContext(aircraft);
        teardrop.OnStart(ctx);

        var route = ctx.Targets.NavigationRoute;
        Assert.Equal(3, route.Count);

        double expected45Reverse = new TrueHeading(teardrop.Waypoints.DownwindHeading.Degrees + 45.0 + 180.0).Degrees;

        // Lead-in (route[1]): bearing from abeam should be ~ reverse-45° entry heading.
        double bearingLeadIn = GeoMath.BearingTo(
            teardrop.Waypoints.DownwindAbeamLat,
            teardrop.Waypoints.DownwindAbeamLon,
            route[1].Latitude,
            route[1].Longitude
        );
        Assert.InRange(GeoMath.AbsBearingDifference(bearingLeadIn, expected45Reverse), 0, 2.0);

        // Abeam (route[2]): should match the abeam point itself.
        Assert.Equal(teardrop.Waypoints.DownwindAbeamLat, route[2].Latitude, precision: 5);
        Assert.Equal(teardrop.Waypoints.DownwindAbeamLon, route[2].Longitude, precision: 5);
    }

    [Fact]
    public void WrongSide_Helicopter_CrossesAtHelicopterTpa_500Agl()
    {
        // Helicopters fly the pattern at 500 AGL per AIM 4-3-3.1.c.
        // PatternAltitudeAgl(Helicopter) = 500, so Waypoints.PatternAltitude = field + 500.
        // Wrong-side helo should cross at that altitude — not 1000, not 1500.
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 1500, 0);
        aircraft.AircraftType = "R44";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);

        var mc = Assert.IsType<MidfieldCrossingPhase>(aircraft.Phases!.Phases[0]);
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        Assert.Equal(AircraftCategory.Helicopter, cat);
        var ctx = MakeContext(aircraft);
        mc.OnStart(ctx);

        double expectedAlt = runway.ElevationFt + 500.0; // helo TPA = 500 AGL
        _output.WriteLine($"Helo crossing target alt: {ctx.Targets.TargetAltitude:F0}ft (expected {expectedAlt:F0}ft = field+500 AGL)");
        Assert.Equal(expectedAlt, ctx.Targets.TargetAltitude);
        Assert.DoesNotContain(aircraft.Phases.Phases, p => p is TeardropReentryPhase);
    }

    [Fact]
    public void TeardropReentry_Jet_OutboundAnchorAt3Nm()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.AircraftType = "B738";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);
        var teardrop = (TeardropReentryPhase)aircraft.Phases!.Phases[1];
        var ctx = MakeContext(aircraft);
        teardrop.OnStart(ctx);

        var outbound = ctx.Targets.NavigationRoute[0];
        double dist = GeoMath.DistanceNm(
            teardrop.Waypoints.DownwindAbeamLat,
            teardrop.Waypoints.DownwindAbeamLon,
            outbound.Latitude,
            outbound.Longitude
        );
        _output.WriteLine($"Jet outbound distance from abeam: {dist:F2}nm (expected 3.0nm)");
        Assert.InRange(dist, 2.95, 3.05);
    }

    [Fact]
    public void TeardropReentry_Turboprop_OutboundAnchorAt2_5Nm()
    {
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.AircraftType = "DH8D";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);
        var teardrop = (TeardropReentryPhase)aircraft.Phases!.Phases[1];
        var ctx = MakeContext(aircraft);
        teardrop.OnStart(ctx);

        var outbound = ctx.Targets.NavigationRoute[0];
        double dist = GeoMath.DistanceNm(
            teardrop.Waypoints.DownwindAbeamLat,
            teardrop.Waypoints.DownwindAbeamLon,
            outbound.Latitude,
            outbound.Longitude
        );
        _output.WriteLine($"Turboprop outbound distance from abeam: {dist:F2}nm (expected 2.5nm)");
        Assert.InRange(dist, 2.45, 2.55);
    }

    [Fact]
    public void TeardropReentry_AltitudeProfileDescendsLinearly()
    {
        // Waypoint altitude restrictions: anchor > lead-in > abeam, ending at TPA.
        var runway = MakeOak28R();
        var aircraft = MakeAircraft(37.63, -122.21, 2500, 0);
        aircraft.AircraftType = "B738";
        aircraft.Phases!.AssignedRunway = runway;

        PatternCommandHandler.TryEnterPattern(aircraft, PatternDirection.Right, PatternEntryLeg.Downwind, runwayId: "28R", finalDistanceNm: null);
        var teardrop = (TeardropReentryPhase)aircraft.Phases!.Phases[1];
        var ctx = MakeContext(aircraft);
        teardrop.OnStart(ctx);

        var route = ctx.Targets.NavigationRoute;
        int anchorAlt = route[0].AltitudeRestriction!.Altitude1Ft;
        int leadInAlt = route[1].AltitudeRestriction!.Altitude1Ft;
        int abeamAlt = route[2].AltitudeRestriction!.Altitude1Ft;
        int tpa = (int)teardrop.Waypoints.PatternAltitude;

        _output.WriteLine($"Altitude profile: anchor={anchorAlt}, lead-in={leadInAlt}, abeam={abeamAlt}, TPA={tpa}");
        Assert.True(anchorAlt > leadInAlt, $"anchor ({anchorAlt}) should be above lead-in ({leadInAlt})");
        Assert.True(leadInAlt > abeamAlt, $"lead-in ({leadInAlt}) should be above abeam ({abeamAlt})");
        Assert.Equal(tpa, abeamAlt);
    }
}
