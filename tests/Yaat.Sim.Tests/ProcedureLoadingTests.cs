using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class ProcedureLoadingTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    // --- Test doubles ---

    private sealed class TestFixLookup : IFixLookup
    {
        private readonly Dictionary<string, (double Lat, double Lon)> _fixes;
        private readonly Dictionary<string, List<string>> _starBodies;

        public TestFixLookup(Dictionary<string, (double Lat, double Lon)>? fixes = null, Dictionary<string, List<string>>? starBodies = null)
        {
            _fixes = fixes ?? [];
            _starBodies = starBodies ?? [];
        }

        public (double Lat, double Lon)? GetFixPosition(string name) => _fixes.TryGetValue(name.ToUpperInvariant(), out var pos) ? pos : null;

        public double? GetAirportElevation(string code) => code == "KSFO" ? 13.0 : null;

        public IReadOnlyList<string> ExpandRoute(string route) => [];

        public IReadOnlyList<string> ExpandRouteForNavigation(string route, string? dep) => [];

        public IReadOnlyList<string>? GetStarBody(string starId) => _starBodies.TryGetValue(starId, out var body) ? body : null;

        public IReadOnlyList<(string Name, IReadOnlyList<string> Fixes)>? GetStarTransitions(string starId) => null;
    }

    private sealed class TestProcedureLookup : IProcedureLookup
    {
        private readonly Dictionary<string, CifpSidProcedure> _sids = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CifpStarProcedure> _stars = new(StringComparer.OrdinalIgnoreCase);

        public void AddSid(CifpSidProcedure sid) => _sids[$"{sid.Airport}:{sid.ProcedureId}"] = sid;

        public void AddStar(CifpStarProcedure star) => _stars[$"{star.Airport}:{star.ProcedureId}"] = star;

        public CifpSidProcedure? GetSid(string airportCode, string sidId) => _sids.TryGetValue($"{airportCode}:{sidId}", out var sid) ? sid : null;

        public IReadOnlyList<CifpSidProcedure> GetSids(string airportCode) => [];

        public CifpStarProcedure? GetStar(string airportCode, string starId) =>
            _stars.TryGetValue($"{airportCode}:{starId}", out var star) ? star : null;

        public IReadOnlyList<CifpStarProcedure> GetStars(string airportCode) => [];
    }

    // --- Helpers ---

    private static TestFixLookup CreateFixLookup()
    {
        return new TestFixLookup(
            fixes: new Dictionary<string, (double Lat, double Lon)>
            {
                ["KSFO"] = (37.619, -122.375),
                ["MOLEN"] = (37.63, -122.35),
                ["PORTE"] = (37.65, -122.30),
                ["SFO"] = (37.619, -122.375),
                ["OAK"] = (37.72, -122.22),
                ["SUNOL"] = (37.58, -121.88),
                ["GROVE"] = (37.55, -121.95),
                ["ARCHI"] = (37.50, -122.00),
                ["BDEGA"] = (38.31, -123.06),
                ["CEDES"] = (37.55, -122.30),
                ["FAITH"] = (37.45, -122.35),
                ["BRIXX"] = (37.40, -122.40),
            },
            starBodies: new Dictionary<string, List<string>> { ["BDEGA3"] = ["BDEGA", "CEDES", "FAITH", "BRIXX"] }
        );
    }

    private static AircraftState CreateIfrAircraft(string route, string departure = "KSFO", string destination = "KSFO")
    {
        return new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Latitude = 37.619,
            Longitude = -122.375,
            Heading = 280,
            Track = 280,
            Altitude = 0,
            IndicatedAirspeed = 0,
            Route = route,
            Departure = departure,
            Destination = destination,
        };
    }

    private static CifpSidProcedure CreateTestSid()
    {
        return new CifpSidProcedure(
            Airport: "KSFO",
            ProcedureId: "PORTE3",
            CommonLegs:
            [
                new CifpLeg(
                    "PORTE",
                    CifpPathTerminator.TF,
                    null,
                    new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 4000),
                    null,
                    CifpFixRole.None,
                    30,
                    null,
                    null,
                    null
                ),
                new CifpLeg(
                    "OAK",
                    CifpPathTerminator.TF,
                    null,
                    new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 10000),
                    new CifpSpeedRestriction(250, true),
                    CifpFixRole.None,
                    40,
                    null,
                    null,
                    null
                ),
            ],
            RunwayTransitions: new Dictionary<string, CifpTransition>
            {
                ["RW28R"] = new(
                    "RW28R",
                    [
                        new CifpLeg(
                            "MOLEN",
                            CifpPathTerminator.TF,
                            null,
                            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 800),
                            null,
                            CifpFixRole.None,
                            10,
                            null,
                            null,
                            null
                        ),
                    ]
                ),
                ["RW28L"] = new(
                    "RW28L",
                    [
                        new CifpLeg(
                            "GROVE",
                            CifpPathTerminator.TF,
                            null,
                            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 900),
                            null,
                            CifpFixRole.None,
                            10,
                            null,
                            null,
                            null
                        ),
                    ]
                ),
            },
            EnrouteTransitions: new Dictionary<string, CifpTransition>
            {
                ["SUNOL"] = new("SUNOL", [new CifpLeg("SUNOL", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 50, null, null, null)]),
            }
        );
    }

    private static CifpStarProcedure CreateTestStar()
    {
        return new CifpStarProcedure(
            Airport: "KSFO",
            ProcedureId: "BDEGA3",
            CommonLegs:
            [
                new CifpLeg(
                    "CEDES",
                    CifpPathTerminator.TF,
                    null,
                    new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrBelow, 12000),
                    null,
                    CifpFixRole.None,
                    20,
                    null,
                    null,
                    null
                ),
                new CifpLeg(
                    "FAITH",
                    CifpPathTerminator.TF,
                    null,
                    new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 8000),
                    new CifpSpeedRestriction(250, true),
                    CifpFixRole.None,
                    30,
                    null,
                    null,
                    null
                ),
            ],
            EnrouteTransitions: new Dictionary<string, CifpTransition>
            {
                ["BDEGA"] = new(
                    "BDEGA",
                    [
                        new CifpLeg(
                            "BDEGA",
                            CifpPathTerminator.TF,
                            null,
                            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 15000),
                            null,
                            CifpFixRole.None,
                            10,
                            null,
                            null,
                            null
                        ),
                    ]
                ),
            },
            RunwayTransitions: new Dictionary<string, CifpTransition>
            {
                ["RW28R"] = new(
                    "RW28R",
                    [
                        new CifpLeg(
                            "BRIXX",
                            CifpPathTerminator.TF,
                            null,
                            new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 4000),
                            null,
                            CifpFixRole.None,
                            40,
                            null,
                            null,
                            null
                        ),
                    ]
                ),
            }
        );
    }

    // --- JARR with CIFP ---

    [Fact]
    public void Jarr_WithCifp_LoadsConstrainedTargets()
    {
        var aircraft = CreateIfrAircraft("KSFO BDEGA3 BDEGA3.BDEGA");
        aircraft.Latitude = 38.5;
        aircraft.Longitude = -123.5;
        aircraft.Altitude = 20000;
        aircraft.Heading = 150;
        aircraft.Track = 150;

        var fixes = CreateFixLookup();
        var procedures = new TestProcedureLookup();
        procedures.AddStar(CreateTestStar());

        var cmd = new JoinStarCommand("BDEGA3", "BDEGA");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Random.Shared, procedureLookup: procedures);

        Assert.True(result.Success);
        Assert.Equal("BDEGA3", aircraft.ActiveStarId);
        Assert.False(aircraft.StarViaMode);

        // Should have BDEGA (transition) + CEDES + FAITH (common)
        Assert.True(aircraft.Targets.NavigationRoute.Count >= 3);

        // First target should be BDEGA with AtOrAbove 15000
        var first = aircraft.Targets.NavigationRoute[0];
        Assert.Equal("BDEGA", first.Name);
        Assert.NotNull(first.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.AtOrAbove, first.AltitudeRestriction!.Type);
        Assert.Equal(15000, first.AltitudeRestriction.Altitude1Ft);

        // FAITH should have speed restriction
        var faith = aircraft.Targets.NavigationRoute.First(t => t.Name == "FAITH");
        Assert.NotNull(faith.SpeedRestriction);
        Assert.Equal(250, faith.SpeedRestriction!.SpeedKts);
    }

    [Fact]
    public void Jarr_SetsActiveStarId_StarViaModeOff()
    {
        var aircraft = CreateIfrAircraft("KSFO BDEGA3 BDEGA3.BDEGA");
        aircraft.Altitude = 20000;

        var fixes = CreateFixLookup();
        var procedures = new TestProcedureLookup();
        procedures.AddStar(CreateTestStar());

        var cmd = new JoinStarCommand("BDEGA3", "BDEGA");
        CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Random.Shared, procedureLookup: procedures);

        Assert.Equal("BDEGA3", aircraft.ActiveStarId);
        Assert.False(aircraft.StarViaMode);
    }

    [Fact]
    public void Jarr_WithoutCifp_FallsBackToNavData()
    {
        var aircraft = CreateIfrAircraft("KSFO BDEGA3");
        aircraft.Latitude = 38.5;
        aircraft.Longitude = -123.5;
        aircraft.Altitude = 20000;
        aircraft.Heading = 150;
        aircraft.Track = 150;

        var fixes = CreateFixLookup();
        // No procedure lookup — forces NavData fallback

        var cmd = new JoinStarCommand("BDEGA3", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Random.Shared);

        Assert.True(result.Success);
        Assert.Equal("BDEGA3", aircraft.ActiveStarId);
        Assert.False(aircraft.StarViaMode);

        // NavData targets have no altitude constraints
        foreach (var target in aircraft.Targets.NavigationRoute)
        {
            Assert.Null(target.AltitudeRestriction);
        }
    }

    // --- SID resolution ---

    [Fact]
    public void TryResolveSidFromCifp_SelectsCorrectRunwayTransition()
    {
        var aircraft = CreateIfrAircraft("PORTE3 SUNOL V244 OAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28R") };

        var fixes = CreateFixLookup();
        var procedures = new TestProcedureLookup();
        procedures.AddSid(CreateTestSid());

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft, fixes, procedures);

        Assert.NotNull(result);
        Assert.Equal("PORTE3", result!.SidId);

        // Order: RW28R transition (MOLEN) → common (PORTE, OAK) → enroute transition? (SUNOL not consumed here because it's second token)
        // Actually: first token PORTE3 is SID, second token SUNOL matches enroute transition
        // So: MOLEN → PORTE → OAK → SUNOL
        Assert.True(result.Targets.Count >= 3);
        Assert.Equal("MOLEN", result.Targets[0].Name);
        Assert.Equal("PORTE", result.Targets[1].Name);
        Assert.Equal("OAK", result.Targets[2].Name);

        // MOLEN should have AtOrAbove 800ft constraint
        Assert.NotNull(result.Targets[0].AltitudeRestriction);
        Assert.Equal(800, result.Targets[0].AltitudeRestriction!.Altitude1Ft);
    }

    [Fact]
    public void TryResolveSidFromCifp_WithEnrouteTransition_IncludesTransitionLegs()
    {
        var aircraft = CreateIfrAircraft("PORTE3 SUNOL V244 OAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28R") };

        var fixes = CreateFixLookup();
        var procedures = new TestProcedureLookup();
        procedures.AddSid(CreateTestSid());

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft, fixes, procedures);

        Assert.NotNull(result);

        // SUNOL (second route token) should match the enroute transition
        var sunol = result!.Targets.FirstOrDefault(t => t.Name == "SUNOL");
        Assert.NotNull(sunol);
    }

    [Fact]
    public void TryResolveSidFromCifp_NoMatchingRunway_UsesCommonOnly()
    {
        var aircraft = CreateIfrAircraft("PORTE3 SUNOL V244 OAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("01L") }; // No match

        var fixes = CreateFixLookup();
        var procedures = new TestProcedureLookup();
        procedures.AddSid(CreateTestSid());

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft, fixes, procedures);

        Assert.NotNull(result);
        // Should have common legs only (PORTE, OAK) + enroute (SUNOL)
        Assert.Equal("PORTE", result!.Targets[0].Name);
    }

    [Fact]
    public void TryResolveSidFromCifp_NoCifpAvailable_ReturnsNull()
    {
        var aircraft = CreateIfrAircraft("PORTE3 SUNOL V244 OAK");

        var fixes = CreateFixLookup();
        // No procedure lookup

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft, fixes, null);

        Assert.Null(result);
    }

    [Fact]
    public void TryResolveSidFromCifp_UnknownSid_ReturnsNull()
    {
        var aircraft = CreateIfrAircraft("BOGUS7 SUNOL V244 OAK");

        var fixes = CreateFixLookup();
        var procedures = new TestProcedureLookup();
        procedures.AddSid(CreateTestSid());

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft, fixes, procedures);

        Assert.Null(result);
    }

    [Fact]
    public void TryResolveSidFromCifp_TargetsCarryConstraints()
    {
        var aircraft = CreateIfrAircraft("PORTE3 V244 OAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28R") };

        var fixes = CreateFixLookup();
        var procedures = new TestProcedureLookup();
        procedures.AddSid(CreateTestSid());

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft, fixes, procedures);

        Assert.NotNull(result);

        // OAK (common leg) should have At 10000ft + 250kts speed
        var oak = result!.Targets.First(t => t.Name == "OAK");
        Assert.NotNull(oak.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.At, oak.AltitudeRestriction!.Type);
        Assert.Equal(10000, oak.AltitudeRestriction.Altitude1Ft);
        Assert.NotNull(oak.SpeedRestriction);
        Assert.Equal(250, oak.SpeedRestriction!.SpeedKts);
    }

    // --- InitialClimbPhase activates SID ---

    [Fact]
    public void InitialClimbPhase_WithDepartureSidId_ActivatesSidViaMode()
    {
        var aircraft = CreateIfrAircraft("PORTE3 SUNOL");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28R") };

        var targets = new List<NavigationTarget>
        {
            new()
            {
                Name = "MOLEN",
                Latitude = 37.63,
                Longitude = -122.35,
                AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 800),
            },
            new()
            {
                Name = "PORTE",
                Latitude = 37.65,
                Longitude = -122.30,
            },
        };

        var phase = new InitialClimbPhase
        {
            Departure = new DefaultDeparture(),
            DepartureRoute = targets,
            DepartureSidId = "PORTE3",
            CruiseAltitude = 35000,
        };

        aircraft.Phases.Phases.Add(phase);

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            FieldElevation = 13,
            DeltaSeconds = 1.0,
            Logger = Logger,
            Runway = aircraft.Phases.AssignedRunway,
        };

        phase.OnStart(ctx);

        Assert.Equal("PORTE3", aircraft.ActiveSidId);
        Assert.True(aircraft.SidViaMode);
        Assert.Equal(2, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("MOLEN", aircraft.Targets.NavigationRoute[0].Name);

        // NavigationTargets should preserve constraints
        Assert.NotNull(aircraft.Targets.NavigationRoute[0].AltitudeRestriction);
    }

    [Fact]
    public void InitialClimbPhase_WithoutDepartureSidId_DoesNotActivateSid()
    {
        var aircraft = CreateIfrAircraft("SUNOL V244 OAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28R") };

        var targets = new List<NavigationTarget>
        {
            new()
            {
                Name = "SUNOL",
                Latitude = 37.58,
                Longitude = -121.88,
            },
        };

        var phase = new InitialClimbPhase { Departure = new DefaultDeparture(), DepartureRoute = targets };

        aircraft.Phases.Phases.Add(phase);

        var ctx = new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = AircraftCategory.Jet,
            FieldElevation = 13,
            DeltaSeconds = 1.0,
            Logger = Logger,
            Runway = aircraft.Phases.AssignedRunway,
        };

        phase.OnStart(ctx);

        Assert.Null(aircraft.ActiveSidId);
        Assert.False(aircraft.SidViaMode);
    }

    // --- ResolveLegsToTargets ---

    [Fact]
    public void ResolveLegsToTargets_ConvertsLegsWithConstraints()
    {
        var fixes = CreateFixLookup();
        var legs = new List<CifpLeg>
        {
            new(
                "MOLEN",
                CifpPathTerminator.TF,
                null,
                new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 800),
                null,
                CifpFixRole.None,
                10,
                null,
                null,
                null
            ),
            new("PORTE", CifpPathTerminator.TF, null, null, new CifpSpeedRestriction(200, true), CifpFixRole.None, 20, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs, fixes);

        Assert.Equal(2, targets.Count);
        Assert.Equal("MOLEN", targets[0].Name);
        Assert.NotNull(targets[0].AltitudeRestriction);
        Assert.Equal("PORTE", targets[1].Name);
        Assert.NotNull(targets[1].SpeedRestriction);
    }

    [Fact]
    public void ResolveLegsToTargets_SkipsUnknownFixes()
    {
        var fixes = CreateFixLookup();
        var legs = new List<CifpLeg>
        {
            new("MOLEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
            new("UNKNOWN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null),
            new("PORTE", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 30, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs, fixes);

        Assert.Equal(2, targets.Count);
        Assert.Equal("MOLEN", targets[0].Name);
        Assert.Equal("PORTE", targets[1].Name);
    }

    [Fact]
    public void ResolveLegsToTargets_DeduplicatesAdjacentFixes()
    {
        var fixes = CreateFixLookup();
        var legs = new List<CifpLeg>
        {
            new("MOLEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
            new("MOLEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null),
            new("PORTE", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 30, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs, fixes);

        Assert.Equal(2, targets.Count);
    }

    // --- GenerateArcPoints ---

    [Fact]
    public void GenerateArcPoints_RightTurn_ProducesCorrectArc()
    {
        double centerLat = 37.0;
        double centerLon = -122.0;
        double radiusNm = 3.0;
        double startBearing = 0; // North
        double endBearing = 90; // East (90° clockwise sweep)

        var points = GeoMath.GenerateArcPoints(centerLat, centerLon, radiusNm, startBearing, endBearing, turnRight: true, stepDeg: 30);

        // 90° sweep at 30° steps = 3 intermediate points (30°, 60°) + end point (90°) = 3 points
        Assert.Equal(3, points.Count);

        // Each point should be approximately radiusNm from center
        foreach (var (lat, lon) in points)
        {
            double dist = GeoMath.DistanceNm(centerLat, centerLon, lat, lon);
            Assert.Equal(radiusNm, dist, precision: 1);
        }

        // Last point should match the end bearing (east of center)
        var last = points[^1];
        double lastBearing = GeoMath.BearingTo(centerLat, centerLon, last.Lat, last.Lon);
        Assert.Equal(endBearing, lastBearing, precision: 0);
    }

    [Fact]
    public void GenerateArcPoints_LeftTurn_ProducesCorrectArc()
    {
        double centerLat = 37.0;
        double centerLon = -122.0;
        double radiusNm = 3.0;
        double startBearing = 90; // East
        double endBearing = 0; // North (90° counter-clockwise sweep)

        var points = GeoMath.GenerateArcPoints(centerLat, centerLon, radiusNm, startBearing, endBearing, turnRight: false, stepDeg: 30);

        // 90° sweep at 30° steps = 3 intermediate points + end point = 3
        Assert.Equal(3, points.Count);

        foreach (var (lat, lon) in points)
        {
            double dist = GeoMath.DistanceNm(centerLat, centerLon, lat, lon);
            Assert.Equal(radiusNm, dist, precision: 1);
        }
    }

    [Fact]
    public void GenerateArcPoints_WrapAround_HandledCorrectly()
    {
        double centerLat = 37.0;
        double centerLon = -122.0;
        double radiusNm = 5.0;
        double startBearing = 350; // NNW
        double endBearing = 10; // NNE (20° clockwise sweep, wrapping through 360)

        var points = GeoMath.GenerateArcPoints(centerLat, centerLon, radiusNm, startBearing, endBearing, turnRight: true, stepDeg: 5);

        // 20° sweep at 5° steps = 3 intermediate + 1 end = 4
        Assert.Equal(4, points.Count);

        foreach (var (lat, lon) in points)
        {
            double dist = GeoMath.DistanceNm(centerLat, centerLon, lat, lon);
            Assert.Equal(radiusNm, dist, precision: 1);
        }
    }

    // --- Arc-aware ResolveLegsToTargets ---

    [Fact]
    public void ResolveLegsToTargets_RfLeg_ExpandsToArcWaypoints()
    {
        // Center at (37.0, -122.0), radius 3nm
        // Previous fix at bearing 0° from center, terminator at bearing 90°
        var centerLat = 37.0;
        var centerLon = -122.0;
        double radius = 3.0;
        var startPt = GeoMath.ProjectPoint(centerLat, centerLon, 0, radius);
        var endPt = GeoMath.ProjectPoint(centerLat, centerLon, 90, radius);

        var fixes = new TestFixLookup(fixes: new Dictionary<string, (double Lat, double Lon)> { ["FIX1"] = startPt, ["FIX2"] = endPt });

        var legs = new List<CifpLeg>
        {
            new("FIX1", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
            new(
                "FIX2",
                CifpPathTerminator.RF,
                'R',
                null,
                null,
                CifpFixRole.None,
                20,
                null,
                null,
                null,
                ArcRadiusNm: radius,
                ArcCenterLat: centerLat,
                ArcCenterLon: centerLon
            ),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs, fixes);

        // Should have: FIX1, intermediate arc points, FIX2
        Assert.True(targets.Count > 2, $"Expected arc expansion, got {targets.Count} targets");
        Assert.Equal("FIX1", targets[0].Name);
        Assert.Equal("FIX2", targets[^1].Name);

        // Intermediate points should be named ARC01, ARC02, etc.
        for (int i = 1; i < targets.Count - 1; i++)
        {
            Assert.StartsWith("ARC", targets[i].Name);
        }
    }

    [Fact]
    public void ResolveLegsToTargets_AfLeg_ExpandsToArcWaypoints()
    {
        // Navaid at center, DME arc of 10nm
        var navaidLat = 37.0;
        var navaidLon = -122.0;
        double rho = 10.0;
        var startPt = GeoMath.ProjectPoint(navaidLat, navaidLon, 180, rho);
        var endPt = GeoMath.ProjectPoint(navaidLat, navaidLon, 270, rho);

        var fixes = new TestFixLookup(
            fixes: new Dictionary<string, (double Lat, double Lon)>
            {
                ["FIX1"] = startPt,
                ["FIX2"] = endPt,
                ["ABQ"] = (navaidLat, navaidLon),
            }
        );

        var legs = new List<CifpLeg>
        {
            new("FIX1", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
            new(
                "FIX2",
                CifpPathTerminator.AF,
                'R',
                null,
                null,
                CifpFixRole.None,
                20,
                null,
                null,
                null,
                RecommendedNavaidId: "ABQ",
                Rho: rho,
                Theta: 270.0
            ),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs, fixes);

        Assert.True(targets.Count > 2, $"Expected arc expansion, got {targets.Count} targets");
        Assert.Equal("FIX1", targets[0].Name);
        Assert.Equal("FIX2", targets[^1].Name);
    }

    [Fact]
    public void ResolveLegsToTargets_SkipsProcedureTurnLegs()
    {
        var fixes = CreateFixLookup();
        var legs = new List<CifpLeg>
        {
            new("MOLEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
            new("PORTE", CifpPathTerminator.PI, null, null, null, CifpFixRole.None, 20, null, null, null),
            new("OAK", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 30, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs, fixes);

        Assert.Equal(2, targets.Count);
        Assert.Equal("MOLEN", targets[0].Name);
        Assert.Equal("OAK", targets[1].Name);
    }

    // --- "B" (both) suffix fallback ---

    [Fact]
    public void TryResolveSidFromCifp_BothSuffix_MatchesLeftRunway()
    {
        var sid = new CifpSidProcedure(
            Airport: "KSFO",
            ProcedureId: "SSTIK5",
            CommonLegs: [new CifpLeg("PORTE", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null)],
            RunwayTransitions: new Dictionary<string, CifpTransition>
            {
                ["RW01B"] = new(
                    "RW01B",
                    [new CifpLeg("SSTIK", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null, IsFlyOver: true)]
                ),
            },
            EnrouteTransitions: new Dictionary<string, CifpTransition>()
        );

        var fixes = new TestFixLookup(
            fixes: new Dictionary<string, (double Lat, double Lon)> { ["SSTIK"] = (37.50, -122.40), ["PORTE"] = (37.65, -122.30) }
        );

        var procedures = new TestProcedureLookup();
        procedures.AddSid(sid);

        var aircraft = CreateIfrAircraft("SSTIK5 PORTE");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("01L") };

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft, fixes, procedures);

        Assert.NotNull(result);
        Assert.Equal("SSTIK", result!.Targets[0].Name);
        Assert.True(result.Targets[0].IsFlyOver);
        Assert.Equal("PORTE", result.Targets[1].Name);
    }

    [Fact]
    public void TryResolveSidFromCifp_BothSuffix_MatchesRightRunway()
    {
        var sid = new CifpSidProcedure(
            Airport: "KSFO",
            ProcedureId: "SSTIK5",
            CommonLegs: [new CifpLeg("PORTE", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null)],
            RunwayTransitions: new Dictionary<string, CifpTransition>
            {
                ["RW01B"] = new(
                    "RW01B",
                    [new CifpLeg("SSTIK", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null, IsFlyOver: true)]
                ),
            },
            EnrouteTransitions: new Dictionary<string, CifpTransition>()
        );

        var fixes = new TestFixLookup(
            fixes: new Dictionary<string, (double Lat, double Lon)> { ["SSTIK"] = (37.50, -122.40), ["PORTE"] = (37.65, -122.30) }
        );

        var procedures = new TestProcedureLookup();
        procedures.AddSid(sid);

        var aircraft = CreateIfrAircraft("SSTIK5 PORTE");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("01R") };

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft, fixes, procedures);

        Assert.NotNull(result);
        Assert.Equal("SSTIK", result!.Targets[0].Name);
    }

    [Fact]
    public void TryResolveSidFromCifp_BothSuffix_ExactMatchTakesPrecedence()
    {
        var sid = new CifpSidProcedure(
            Airport: "KSFO",
            ProcedureId: "SSTIK5",
            CommonLegs: [new CifpLeg("PORTE", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null)],
            RunwayTransitions: new Dictionary<string, CifpTransition>
            {
                ["RW01L"] = new("RW01L", [new CifpLeg("MOLEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null)]),
                ["RW01B"] = new(
                    "RW01B",
                    [new CifpLeg("SSTIK", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null, IsFlyOver: true)]
                ),
            },
            EnrouteTransitions: new Dictionary<string, CifpTransition>()
        );

        var fixes = new TestFixLookup(
            fixes: new Dictionary<string, (double Lat, double Lon)>
            {
                ["MOLEN"] = (37.63, -122.35),
                ["SSTIK"] = (37.50, -122.40),
                ["PORTE"] = (37.65, -122.30),
            }
        );

        var procedures = new TestProcedureLookup();
        procedures.AddSid(sid);

        var aircraft = CreateIfrAircraft("SSTIK5 PORTE");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("01L") };

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft, fixes, procedures);

        Assert.NotNull(result);
        // Exact match RW01L (MOLEN) should win over RW01B (SSTIK)
        Assert.Equal("MOLEN", result!.Targets[0].Name);
    }

    [Fact]
    public void Jarr_BothSuffix_MatchesRunwayTransition()
    {
        var star = new CifpStarProcedure(
            Airport: "KSFO",
            ProcedureId: "BDEGA3",
            CommonLegs: [new CifpLeg("CEDES", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null)],
            EnrouteTransitions: new Dictionary<string, CifpTransition>
            {
                ["BDEGA"] = new("BDEGA", [new CifpLeg("BDEGA", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null)]),
            },
            RunwayTransitions: new Dictionary<string, CifpTransition>
            {
                ["RW28B"] = new("RW28B", [new CifpLeg("BRIXX", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 30, null, null, null)]),
            }
        );

        var fixes = new TestFixLookup(
            fixes: new Dictionary<string, (double Lat, double Lon)>
            {
                ["BDEGA"] = (38.31, -123.06),
                ["CEDES"] = (37.55, -122.30),
                ["BRIXX"] = (37.40, -122.40),
            }
        );

        var procedures = new TestProcedureLookup();
        procedures.AddStar(star);

        var aircraft = CreateIfrAircraft("KSFO BDEGA3 BDEGA3.BDEGA");
        aircraft.Latitude = 38.5;
        aircraft.Longitude = -123.5;
        aircraft.Altitude = 20000;
        aircraft.Heading = 150;
        aircraft.Track = 150;
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28L") };

        var cmd = new JoinStarCommand("BDEGA3", "BDEGA");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, null, fixes, Random.Shared, procedureLookup: procedures);

        Assert.True(result.Success);
        var brixx = aircraft.Targets.NavigationRoute.FirstOrDefault(t => t.Name == "BRIXX");
        Assert.NotNull(brixx);
    }

    // --- Helper ---

    private static RunwayInfo MakeRunway(string designator) =>
        TestRunwayFactory.Make(
            designator: designator,
            airportId: "KSFO",
            thresholdLat: 37.619,
            thresholdLon: -122.375,
            heading: 280,
            elevationFt: 13
        );
}
