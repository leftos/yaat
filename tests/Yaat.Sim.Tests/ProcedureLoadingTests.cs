using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class ProcedureLoadingTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    public ProcedureLoadingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    // --- Helpers ---

    private static readonly Dictionary<string, (double Lat, double Lon)> DefaultFixes = new(StringComparer.OrdinalIgnoreCase)
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
    };

    private static readonly Dictionary<string, IReadOnlyList<string>> DefaultStarBodies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BDEGA3"] = ["BDEGA", "CEDES", "FAITH", "BRIXX"],
    };

    private static NavigationDatabase CreateNavDb(
        CifpSidProcedure? sid = null,
        CifpStarProcedure? star = null,
        Dictionary<string, (double Lat, double Lon)>? extraFixes = null
    )
    {
        Dictionary<string, (double Lat, double Lon)> fixes;
        if (extraFixes is not null)
        {
            fixes = new Dictionary<string, (double Lat, double Lon)>(DefaultFixes, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in extraFixes)
            {
                fixes[k] = v;
            }
        }
        else
        {
            fixes = DefaultFixes;
        }

        return TestNavDbFactory.WithFixesAndProcedures(fixes, sid is not null ? [sid] : null, star is not null ? [star] : null, DefaultStarBodies);
    }

    private static AircraftState CreateIfrAircraft(string route, string departure = "KSFO", string destination = "KSFO")
    {
        return new AircraftState
        {
            Callsign = "UAL123",
            AircraftType = "B738",
            Latitude = 37.619,
            Longitude = -122.375,
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
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
        aircraft.TrueHeading = new TrueHeading(150);
        aircraft.TrueTrack = new TrueHeading(150);

        var navDb = CreateNavDb(star: CreateTestStar());
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new JoinStarCommand("BDEGA3", "BDEGA");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

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

        var navDb = CreateNavDb(star: CreateTestStar());
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new JoinStarCommand("BDEGA3", "BDEGA");
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Equal("BDEGA3", aircraft.ActiveStarId);
        Assert.False(aircraft.StarViaMode);
    }

    // --- SID resolution ---

    [Fact]
    public void TryResolveSidFromCifp_SelectsCorrectRunwayTransition()
    {
        var aircraft = CreateIfrAircraft("PORTE3 SUNOL V244 OAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28R") };

        var navDb = CreateNavDb(sid: CreateTestSid());
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.NotNull(result);
        Assert.Equal("PORTE3", result.SidId);

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

        var navDb = CreateNavDb(sid: CreateTestSid());
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.NotNull(result);

        // SUNOL (second route token) should match the enroute transition
        var sunol = result.Targets.FirstOrDefault(t => t.Name == "SUNOL");
        Assert.NotNull(sunol);
    }

    [Fact]
    public void TryResolveSidFromCifp_NoMatchingRunway_UsesCommonOnly()
    {
        var aircraft = CreateIfrAircraft("PORTE3 SUNOL V244 OAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("01L") }; // No match

        var navDb = CreateNavDb(sid: CreateTestSid());
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.NotNull(result);
        // Should have common legs only (PORTE, OAK) + enroute (SUNOL)
        Assert.Equal("PORTE", result.Targets[0].Name);
    }

    [Fact]
    public void TryResolveSidFromCifp_UnknownSid_ReturnsNull()
    {
        var aircraft = CreateIfrAircraft("BOGUS7 SUNOL V244 OAK");

        var navDb = CreateNavDb(sid: CreateTestSid());
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.Null(result);
    }

    [Fact]
    public void TryResolveSidFromCifp_TargetsCarryConstraints()
    {
        var aircraft = CreateIfrAircraft("PORTE3 V244 OAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28R") };

        var navDb = CreateNavDb(sid: CreateTestSid());
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.NotNull(result);

        // OAK (common leg) should have At 10000ft + 250kts speed
        var oak = result.Targets.First(t => t.Name == "OAK");
        Assert.NotNull(oak.AltitudeRestriction);
        Assert.Equal(CifpAltitudeRestrictionType.At, oak.AltitudeRestriction.Type);
        Assert.Equal(10000, oak.AltitudeRestriction.Altitude1Ft);
        Assert.NotNull(oak.SpeedRestriction);
        Assert.Equal(250, oak.SpeedRestriction.SpeedKts);
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
        var navDb = CreateNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);
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

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);

        Assert.Equal(2, targets.Count);
        Assert.Equal("MOLEN", targets[0].Name);
        Assert.NotNull(targets[0].AltitudeRestriction);
        Assert.Equal("PORTE", targets[1].Name);
        Assert.NotNull(targets[1].SpeedRestriction);
    }

    [Fact]
    public void ResolveLegsToTargets_SkipsUnknownFixes()
    {
        var navDb = CreateNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var legs = new List<CifpLeg>
        {
            new("MOLEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
            new("UNKNOWN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null),
            new("PORTE", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 30, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);

        Assert.Equal(2, targets.Count);
        Assert.Equal("MOLEN", targets[0].Name);
        Assert.Equal("PORTE", targets[1].Name);
    }

    [Fact]
    public void ResolveLegsToTargets_DeduplicatesAdjacentFixes()
    {
        var navDb = CreateNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var legs = new List<CifpLeg>
        {
            new("MOLEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
            new("MOLEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null),
            new("PORTE", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 30, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);

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
        var startPt = GeoMath.ProjectPoint(centerLat, centerLon, new TrueHeading(0), radius);
        var endPt = GeoMath.ProjectPoint(centerLat, centerLon, new TrueHeading(90), radius);

        var navDb = CreateNavDb(extraFixes: new Dictionary<string, (double Lat, double Lon)> { ["FIX1"] = startPt, ["FIX2"] = endPt });
        using var _ = NavigationDatabase.ScopedOverride(navDb);

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

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);

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
        var startPt = GeoMath.ProjectPoint(navaidLat, navaidLon, new TrueHeading(180), rho);
        var endPt = GeoMath.ProjectPoint(navaidLat, navaidLon, new TrueHeading(270), rho);

        var navDb = CreateNavDb(
            extraFixes: new Dictionary<string, (double Lat, double Lon)>
            {
                ["FIX1"] = startPt,
                ["FIX2"] = endPt,
                ["ABQ"] = (navaidLat, navaidLon),
            }
        );
        using var _ = NavigationDatabase.ScopedOverride(navDb);

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

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);

        Assert.True(targets.Count > 2, $"Expected arc expansion, got {targets.Count} targets");
        Assert.Equal("FIX1", targets[0].Name);
        Assert.Equal("FIX2", targets[^1].Name);
    }

    [Fact]
    public void ResolveLegsToTargets_SkipsProcedureTurnLegs()
    {
        var navDb = CreateNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var legs = new List<CifpLeg>
        {
            new("MOLEN", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
            new("PORTE", CifpPathTerminator.PI, null, null, null, CifpFixRole.None, 20, null, null, null),
            new("OAK", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 30, null, null, null),
        };

        var targets = DepartureClearanceHandler.ResolveLegsToTargets(legs);

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

        var navDb = CreateNavDb(
            sid: sid,
            extraFixes: new Dictionary<string, (double Lat, double Lon)> { ["SSTIK"] = (37.50, -122.40), ["PORTE"] = (37.65, -122.30) }
        );
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var aircraft = CreateIfrAircraft("SSTIK5 PORTE");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("01L") };

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.NotNull(result);
        Assert.Equal("SSTIK", result.Targets[0].Name);
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

        var navDb = CreateNavDb(
            sid: sid,
            extraFixes: new Dictionary<string, (double Lat, double Lon)> { ["SSTIK"] = (37.50, -122.40), ["PORTE"] = (37.65, -122.30) }
        );
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var aircraft = CreateIfrAircraft("SSTIK5 PORTE");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("01R") };

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.NotNull(result);
        Assert.Equal("SSTIK", result.Targets[0].Name);
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

        var navDb = CreateNavDb(
            sid: sid,
            extraFixes: new Dictionary<string, (double Lat, double Lon)>
            {
                ["MOLEN"] = (37.63, -122.35),
                ["SSTIK"] = (37.50, -122.40),
                ["PORTE"] = (37.65, -122.30),
            }
        );
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var aircraft = CreateIfrAircraft("SSTIK5 PORTE");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("01L") };

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.NotNull(result);
        // Exact match RW01L (MOLEN) should win over RW01B (SSTIK)
        Assert.Equal("MOLEN", result.Targets[0].Name);
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

        var navDb = CreateNavDb(
            star: star,
            extraFixes: new Dictionary<string, (double Lat, double Lon)>
            {
                ["BDEGA"] = (38.31, -123.06),
                ["CEDES"] = (37.55, -122.30),
                ["BRIXX"] = (37.40, -122.40),
            }
        );
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var aircraft = CreateIfrAircraft("KSFO BDEGA3 BDEGA3.BDEGA");
        aircraft.Latitude = 38.5;
        aircraft.Longitude = -123.5;
        aircraft.Altitude = 20000;
        aircraft.TrueHeading = new TrueHeading(150);
        aircraft.TrueTrack = new TrueHeading(150);
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28L") };

        var cmd = new JoinStarCommand("BDEGA3", "BDEGA");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        var brixx = aircraft.Targets.NavigationRoute.FirstOrDefault(t => t.Name == "BRIXX");
        Assert.NotNull(brixx);
    }

    // --- Radar vectors SID detection ---

    [Fact]
    public void IsRadarVectorsSid_VmLastLeg_ReturnsTrue()
    {
        var rwLegs = new List<CifpLeg>
        {
            new("", CifpPathTerminator.CA, null, null, null, CifpFixRole.None, 10, 278.0, null, null),
            new("", CifpPathTerminator.VM, null, null, null, CifpFixRole.None, 20, 315.0, null, null),
        };

        Assert.True(DepartureClearanceHandler.IsRadarVectorsSid(rwLegs, []));
    }

    [Fact]
    public void IsRadarVectorsSid_TfLastLeg_ReturnsFalse()
    {
        var rwLegs = new List<CifpLeg>
        {
            new("", CifpPathTerminator.VA, null, null, null, CifpFixRole.None, 10, 278.0, null, null),
            new("PORTE", CifpPathTerminator.DF, null, null, null, CifpFixRole.None, 20, null, null, null),
        };
        var commonLegs = new List<CifpLeg> { new("OAK", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 30, null, null, null) };

        Assert.False(DepartureClearanceHandler.IsRadarVectorsSid(rwLegs, commonLegs));
    }

    [Fact]
    public void IsRadarVectorsSid_CommonLegsEndInVm_ReturnsTrue()
    {
        var rwLegs = new List<CifpLeg> { new("", CifpPathTerminator.CA, null, null, null, CifpFixRole.None, 10, 280.0, null, null) };
        var commonLegs = new List<CifpLeg> { new("", CifpPathTerminator.VM, null, null, null, CifpFixRole.None, 20, 315.0, null, null) };

        Assert.True(DepartureClearanceHandler.IsRadarVectorsSid(rwLegs, commonLegs));
    }

    [Fact]
    public void TryResolveSidFromCifp_RadarVectorsSid_SkipsCifpLegs_ReturnsPostSidFixes()
    {
        // Radar vectors SID: CA → VM (heading 315), no common legs, no enroute transitions
        var sid = new CifpSidProcedure(
            Airport: "KOAK",
            ProcedureId: "NIMI5",
            CommonLegs: [],
            RunwayTransitions: new Dictionary<string, CifpTransition>
            {
                ["RW28B"] = new(
                    "RW28B",
                    [
                        new CifpLeg("", CifpPathTerminator.CA, null, null, null, CifpFixRole.None, 10, 278.0, null, null),
                        new CifpLeg("", CifpPathTerminator.VM, null, null, null, CifpFixRole.None, 20, 315.0, null, null),
                    ]
                ),
            },
            EnrouteTransitions: new Dictionary<string, CifpTransition>()
        );

        var navDb = CreateNavDb(sid: sid);
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var aircraft = CreateIfrAircraft("NIMI5 OAK V6 SAC", departure: "KOAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28R") };

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.NotNull(result);
        // SidId should be null — no via-mode constraints for radar vectors SIDs
        Assert.Null(result.SidId);
        // Should NOT contain any internal CIFP fixes (there are none for this SID anyway)
        // Should contain post-SID enroute fix OAK (from route "NIMI5 OAK V6 SAC")
        Assert.Contains(result.Targets, t => t.Name == "OAK");
        // DepartureHeadingMagnetic should be 315 from the VM leg
        Assert.NotNull(result.DepartureHeadingMagnetic);
        Assert.Equal(315.0, result.DepartureHeadingMagnetic!.Value, 1);
    }

    [Fact]
    public void TryResolveSidFromCifp_RadarVectorsSid_ExtractsHeading()
    {
        var sid = new CifpSidProcedure(
            Airport: "KOAK",
            ProcedureId: "RVTEST1",
            CommonLegs: [],
            RunwayTransitions: new Dictionary<string, CifpTransition>
            {
                ["RW10B"] = new("RW10B", [new CifpLeg("", CifpPathTerminator.VM, null, null, null, CifpFixRole.None, 10, 098.0, null, null)]),
            },
            EnrouteTransitions: new Dictionary<string, CifpTransition>()
        );

        var navDb = CreateNavDb(sid: sid);
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var aircraft = CreateIfrAircraft("RVTEST1 OAK", departure: "KOAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("10L") };

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.NotNull(result);
        Assert.Equal(98.0, result.DepartureHeadingMagnetic!.Value, 1);
    }

    [Fact]
    public void TryResolveSidFromCifp_RadarVectorsSid_NoPostSidFixes_ReturnsWithEmptyTargets()
    {
        var sid = new CifpSidProcedure(
            Airport: "KOAK",
            ProcedureId: "RVONLY1",
            CommonLegs: [],
            RunwayTransitions: new Dictionary<string, CifpTransition>
            {
                ["RW28B"] = new("RW28B", [new CifpLeg("", CifpPathTerminator.VM, null, null, null, CifpFixRole.None, 10, 315.0, null, null)]),
            },
            EnrouteTransitions: new Dictionary<string, CifpTransition>()
        );

        var navDb = CreateNavDb(sid: sid);
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        // Route is just the SID name — no post-SID enroute fixes
        var aircraft = CreateIfrAircraft("RVONLY1", departure: "KOAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28R") };

        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        // Should still return a result with heading even if no nav targets
        Assert.NotNull(result);
        Assert.Empty(result.Targets);
        Assert.Equal(315.0, result.DepartureHeadingMagnetic!.Value, 1);
    }

    [Fact]
    public void TryResolveSidFromCifp_NonRadarVectorsSid_StillReturnsTargets()
    {
        // Regression guard: PORTE3 (normal SID with TF legs) should still work
        var aircraft = CreateIfrAircraft("PORTE3 SUNOL V244 OAK");
        aircraft.Phases = new PhaseList { AssignedRunway = MakeRunway("28R") };

        var navDb = CreateNavDb(sid: CreateTestSid());
        using var _ = NavigationDatabase.ScopedOverride(navDb);
        var result = DepartureClearanceHandler.TryResolveSidFromCifp(aircraft);

        Assert.NotNull(result);
        Assert.Equal("PORTE3", result.SidId);
        Assert.True(result.Targets.Count >= 3);
        Assert.Equal("MOLEN", result.Targets[0].Name);
        Assert.Null(result.DepartureHeadingMagnetic);
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
