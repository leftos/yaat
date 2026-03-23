using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class ApproachCommandHandlerTests
{
    public ApproachCommandHandlerTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(
        double heading = 280,
        double altitude = 3000,
        double lat = 37.75,
        double lon = -122.35,
        string destination = "OAK",
        double? speed = null
    )
    {
        var ac = new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(heading),
            Altitude = altitude,
            Latitude = lat,
            Longitude = lon,
            Destination = destination,
        };

        if (speed is not null)
        {
            ac.Targets.TargetSpeed = speed;
        }

        return ac;
    }

    private static RunwayInfo MakeRunway(string designator = "28R", string airportId = "OAK", double heading = 280)
    {
        return TestRunwayFactory.Make(
            designator: designator,
            airportId: airportId,
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: heading,
            elevationFt: 9
        );
    }

    // --- CAPP basic ---

    [Fact]
    public void Capp_Basic_CreatesPhaseSequence()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);
        Assert.Contains("I28R", result.Message);
    }

    [Fact]
    public void Capp_SetsActiveApproach()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.Equal("I28R", aircraft.Phases.ActiveApproach.ApproachId);
        Assert.Equal("OAK", aircraft.Phases.ActiveApproach.AirportCode);
        Assert.Equal("28R", aircraft.Phases.ActiveApproach.RunwayId);
    }

    [Fact]
    public void Capp_CancelsSpeedRestriction()
    {
        var aircraft = MakeAircraft(speed: 210);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        Assert.Equal(210, aircraft.Targets.TargetSpeed);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Null(aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void Capp_WithAtFix_PrependsFixToApproachNav()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, "SUNOL", 37.5, -121.8, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        var navPhase = aircraft.Phases!.Phases.OfType<ApproachNavigationPhase>().Single();
        Assert.Equal("SUNOL", navPhase.Fixes[0].Name);
    }

    [Fact]
    public void Capp_WithDctFix_PrependsFixToApproachNav()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, "SUNOL", 37.5, -121.8, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        var navPhase = aircraft.Phases!.Phases.OfType<ApproachNavigationPhase>().Single();
        Assert.Equal("SUNOL", navPhase.Fixes[0].Name);
    }

    [Fact]
    public void Capp_WithCrossFixAltitude_SetsAltitude()
    {
        var aircraft = MakeAircraft(altitude: 5000);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, "SUNOL", 37.5, -121.8, 3400, CrossFixAltitudeType.At);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(3400, aircraft.Targets.TargetAltitude);
    }

    // --- Intercept angle (no dispatch-time rejection) ---

    [Fact]
    public void Capp_SucceedsRegardlessOfInterceptAngle()
    {
        // Aircraft heading 180 vs final course 280 = 100° — should still succeed at dispatch
        // Intercept angle validation happens at capture time, not dispatch time
        var aircraft = MakeAircraft(heading: 180);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
    }

    // --- CAPP heading intercept (issue #75) ---

    [Fact]
    public void Capp_WithAssignedHeading_UsesInterceptPhase()
    {
        var aircraft = MakeAircraft();
        aircraft.Targets.TargetTrueHeading = new TrueHeading(340);
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(340);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);
        Assert.Equal(3, aircraft.Phases.Phases.Count);
        Assert.IsType<InterceptCoursePhase>(aircraft.Phases.Phases[0]);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases.Phases[1]);
        Assert.IsType<LandingPhase>(aircraft.Phases.Phases[2]);
        Assert.Equal(340, aircraft.Targets.TargetTrueHeading?.Degrees);
        Assert.Empty(aircraft.Targets.NavigationRoute);
    }

    [Fact]
    public void Capp_WithAssignedHeadingAndCrossFixAlt_AppliesCrossAlt()
    {
        var aircraft = MakeAircraft(altitude: 5000);
        aircraft.Targets.TargetTrueHeading = new TrueHeading(340);
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(340);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, 3000, CrossFixAltitudeType.At);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(3000, aircraft.Targets.TargetAltitude);
        Assert.IsType<InterceptCoursePhase>(aircraft.Phases!.Phases[0]);
    }

    [Fact]
    public void Capp_WithAssignedHeadingAndAtFix_UsesFixNavigation()
    {
        var aircraft = MakeAircraft();
        aircraft.Targets.TargetTrueHeading = new TrueHeading(340);
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(340);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, "SUNOL", 37.5, -121.8, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        var navPhase = aircraft.Phases!.Phases.OfType<ApproachNavigationPhase>().Single();
        Assert.Equal("SUNOL", navPhase.Fixes[0].Name);
    }

    [Fact]
    public void Capp_WithAssignedHeadingAndDctFix_UsesFixNavigation()
    {
        var aircraft = MakeAircraft();
        aircraft.Targets.TargetTrueHeading = new TrueHeading(340);
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(340);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, "SUNOL", 37.5, -121.8, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        var navPhase = aircraft.Phases!.Phases.OfType<ApproachNavigationPhase>().Single();
        Assert.Equal("SUNOL", navPhase.Fixes[0].Name);
    }

    [Fact]
    public void Capp_WithoutAssignedHeading_UsesFixNavigation()
    {
        var aircraft = MakeAircraft();
        Assert.Null(aircraft.Targets.TargetTrueHeading);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains(aircraft.Phases!.Phases, p => p is ApproachNavigationPhase);
    }

    [Fact]
    public void Capp_WithTargetHeadingButNoAssignedHeading_UsesFixNavigation()
    {
        // Regression: TargetHeading is set by physics every tick during route navigation,
        // but AssignedHeading is null because no controller heading command was issued.
        // CAPP must use fix navigation, not intercept.
        var aircraft = MakeAircraft();
        aircraft.Targets.TargetTrueHeading = new TrueHeading(280);
        Assert.Null(aircraft.Targets.AssignedMagneticHeading);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains(aircraft.Phases!.Phases, p => p is ApproachNavigationPhase);
        Assert.DoesNotContain(aircraft.Phases.Phases, p => p is InterceptCoursePhase);
    }

    [Fact]
    public void Capp_WithAssignedHeading_ClearsSpeedRestriction()
    {
        var aircraft = MakeAircraft(speed: 210);
        aircraft.Targets.TargetTrueHeading = new TrueHeading(340);
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(340);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, null, null);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Null(aircraft.Targets.TargetSpeed);
    }

    // --- JAPP ---

    [Fact]
    public void Japp_CreatesPhaseSequence()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new JoinApproachCommand("ILS28R", null, false);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);
        Assert.Contains("Join", result.Message);
    }

    [Fact]
    public void Japp_CancelsSpeedRestriction()
    {
        var aircraft = MakeAircraft(speed: 210);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new JoinApproachCommand("ILS28R", null, false);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Null(aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void Japp_SucceedsRegardlessOfInterceptAngle()
    {
        var aircraft = MakeAircraft(heading: 180);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new JoinApproachCommand("ILS28R", null, false);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
    }

    // --- CAPPSI / JAPPSI (straight-in) ---

    [Fact]
    public void Cappsi_SkipsHoldInLieu()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDbWithHoldInLieu();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachStraightInCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("straight-in", result.Message);

        // No holding pattern phase
        Assert.NotNull(aircraft.Phases);
        Assert.DoesNotContain(aircraft.Phases.Phases, p => p is HoldingPatternPhase);
    }

    [Fact]
    public void Jappsi_SkipsHoldInLieu()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDbWithHoldInLieu();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new JoinApproachStraightInCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.DoesNotContain(aircraft.Phases!.Phases, p => p is HoldingPatternPhase);
    }

    // --- Hold-in-lieu ---

    [Fact]
    public void Japp_WithHoldInLieu_InsertsHoldPhase()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDbWithHoldInLieu();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new JoinApproachCommand("ILS28R", null, false);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases);
        Assert.Contains(aircraft.Phases.Phases, p => p is HoldingPatternPhase);

        var holdPhase = aircraft.Phases.Phases.OfType<HoldingPatternPhase>().First();
        Assert.Equal(1, holdPhase.MaxCircuits);
    }

    // --- PTAC ---

    [Fact]
    public void Ptac_SetsHeadingAndAltitude()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDbRunwayAndApproachOnly();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new PositionTurnAltitudeClearanceCommand(new MagneticHeading(340), 2500, "ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(340, aircraft.Targets.TargetTrueHeading?.Degrees);
        Assert.Equal(2500, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void Ptac_CreatesInterceptPhaseSequence()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDbRunwayAndApproachOnly();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new PositionTurnAltitudeClearanceCommand(new MagneticHeading(340), 2500, "ILS28R");
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.NotNull(aircraft.Phases);
        Assert.Equal(3, aircraft.Phases.Phases.Count);
        Assert.IsType<InterceptCoursePhase>(aircraft.Phases.Phases[0]);
        Assert.IsType<FinalApproachPhase>(aircraft.Phases.Phases[1]);
        Assert.IsType<LandingPhase>(aircraft.Phases.Phases[2]);
    }

    [Fact]
    public void Ptac_CancelsSpeedRestriction()
    {
        var aircraft = MakeAircraft(speed: 210);
        var navDb = MakeNavDbRunwayAndApproachOnly();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new PositionTurnAltitudeClearanceCommand(new MagneticHeading(340), 2500, "ILS28R");
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Null(aircraft.Targets.TargetSpeed);
    }

    [Fact]
    public void Ptac_SetsActiveApproach()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDbRunwayAndApproachOnly();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new PositionTurnAltitudeClearanceCommand(new MagneticHeading(340), 2500, "ILS28R");
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.Equal("I28R", aircraft.Phases.ActiveApproach.ApproachId);
    }

    // --- PTAC PH/PA support (issue #76) ---

    [Fact]
    public void Ptac_PresentHeading_UsesAircraftHeading()
    {
        var aircraft = MakeAircraft(heading: 195);
        var navDb = MakeNavDbRunwayAndApproachOnly();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new PositionTurnAltitudeClearanceCommand(null, 2500, "ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(195, aircraft.Targets.TargetTrueHeading?.Degrees);
    }

    [Fact]
    public void Ptac_PresentAltitude_UsesAircraftAltitude()
    {
        var aircraft = MakeAircraft(altitude: 4500);
        var navDb = MakeNavDbRunwayAndApproachOnly();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new PositionTurnAltitudeClearanceCommand(new MagneticHeading(280), null, "ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(4500, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void Ptac_NoApproachId_AutoResolves()
    {
        var aircraft = MakeAircraft();
        aircraft.ExpectedApproach = "ILS28R";
        var navDb = MakeNavDbRunwayAndApproachOnly();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new PositionTurnAltitudeClearanceCommand(new MagneticHeading(280), 2500, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.Equal("I28R", aircraft.Phases.ActiveApproach.ApproachId);
    }

    [Fact]
    public void Ptac_AllPresent_BarePtac()
    {
        var aircraft = MakeAircraft(heading: 310, altitude: 3500);
        aircraft.ExpectedApproach = "ILS28R";
        var navDb = MakeNavDbRunwayAndApproachOnly();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new PositionTurnAltitudeClearanceCommand(null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(310, aircraft.Targets.TargetTrueHeading?.Degrees);
        Assert.Equal(3500, aircraft.Targets.TargetAltitude);
        Assert.IsType<InterceptCoursePhase>(aircraft.Phases!.Phases[0]);
    }

    [Fact]
    public void Ptac_ExplicitValues_StillWork()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDbRunwayAndApproachOnly();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new PositionTurnAltitudeClearanceCommand(new MagneticHeading(340), 2500, "ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(340, aircraft.Targets.TargetTrueHeading?.Degrees);
        Assert.Equal(2500, aircraft.Targets.TargetAltitude);
    }

    // --- ApproachNavigationPhase ---

    [Fact]
    public void ApproachNavPhase_NavigatesThroughFixes()
    {
        var fixes = new List<ApproachFix> { new("FIX1", 37.80, -122.30), new("FIX2", 37.75, -122.25) };

        var phase = new ApproachNavigationPhase { Fixes = fixes };
        var aircraft = MakeAircraft(lat: 37.80, lon: -122.30);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);

        // Aircraft is at FIX1 — should advance to FIX2
        bool done = phase.OnTick(ctx);
        Assert.False(done);
        Assert.Contains(aircraft.Targets.NavigationRoute, t => t.Name == "FIX2");
    }

    [Fact]
    public void ApproachNavPhase_CompletesAtLastFix()
    {
        var fixes = new List<ApproachFix> { new("FIX1", 37.80, -122.30) };

        var phase = new ApproachNavigationPhase { Fixes = fixes };
        var aircraft = MakeAircraft(lat: 37.80, lon: -122.30);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);

        // Aircraft is at the only fix — should complete
        bool done = phase.OnTick(ctx);
        Assert.True(done);
    }

    [Fact]
    public void ApproachNavPhase_AppliesAltitudeRestriction()
    {
        var altRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.At, 3000);
        var fixes = new List<ApproachFix> { new("FIX1", 37.80, -122.30, altRestriction), new("FIX2", 37.75, -122.25) };

        var phase = new ApproachNavigationPhase { Fixes = fixes };
        var aircraft = MakeAircraft(altitude: 5000, lat: 37.85, lon: -122.35);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);

        Assert.Equal(3000, aircraft.Targets.TargetAltitude);
    }

    [Fact]
    public void ApproachNavPhase_ClearsPhaseOnHeadingCommand()
    {
        var phase = new ApproachNavigationPhase { Fixes = [] };
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
    }

    [Fact]
    public void ApproachNavPhase_AllowsLandingClearance()
    {
        var phase = new ApproachNavigationPhase { Fixes = [] };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClearedToLand));
    }

    [Fact]
    public void ApproachNavPhase_AllowsSpeedCommand()
    {
        var phase = new ApproachNavigationPhase { Fixes = [] };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Speed));
    }

    [Fact]
    public void ApproachNavPhase_AllowsAltitudeCommand()
    {
        var phase = new ApproachNavigationPhase { Fixes = [] };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.DescendMaintain));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClimbMaintain));
    }

    [Fact]
    public void InterceptPhase_AllowsSpeedCommand()
    {
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = new TrueHeading(280),
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
        };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Speed));
    }

    [Fact]
    public void InterceptPhase_ClearsPhaseOnHeadingCommand()
    {
        var phase = new InterceptCoursePhase
        {
            FinalApproachCourse = new TrueHeading(280),
            ThresholdLat = 37.72,
            ThresholdLon = -122.22,
        };
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
    }

    [Fact]
    public void ApproachNavPhase_AppliesGlideSlopeInterceptAltitude()
    {
        var altRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.GlideSlopeIntercept, 1800);
        var fixes = new List<ApproachFix> { new("FIX1", 37.80, -122.30, altRestriction), new("FIX2", 37.75, -122.25) };

        var phase = new ApproachNavigationPhase { Fixes = fixes };
        var aircraft = MakeAircraft(altitude: 3000, lat: 37.85, lon: -122.35);
        var ctx = MakeContext(aircraft);

        phase.OnStart(ctx);

        Assert.Equal(1800, aircraft.Targets.TargetAltitude);
    }

    // --- CAPP auto-resolve from ExpectedApproach ---

    [Fact]
    public void Capp_BareWithExpectedApproach_ResolvesFromExpectedApproach()
    {
        var aircraft = MakeAircraft();
        aircraft.ExpectedApproach = "I28R";
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand(null, null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        Assert.Equal("I28R", aircraft.Phases.ActiveApproach.ApproachId);
    }

    [Fact]
    public void Capp_BareWithExpectedApproachAndDestinationRunway_PrefersExpectedApproach()
    {
        var aircraft = MakeAircraft();
        aircraft.ExpectedApproach = "I28R";
        aircraft.DestinationRunway = "28L";
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand(null, null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.NotNull(aircraft.Phases?.ActiveApproach);
        // ExpectedApproach "I28R" wins over DestinationRunway "28L"
        Assert.Equal("I28R", aircraft.Phases.ActiveApproach.ApproachId);
    }

    [Fact]
    public void Capp_BareCompound_ReadbackContainsResolvedApproachId()
    {
        var aircraft = MakeAircraft();
        aircraft.ExpectedApproach = "I28R";
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        // Use DispatchCompound (the compound command path) with null ApproachId.
        // Before the fix, NaturalDescription was pre-computed with a blank approach ID.
        var cappCmd = new ClearedApproachCommand(null, null, false, null, null, null, null, null, null, null, null);
        var compound = new CompoundCommand([new ParsedBlock(null, [cappCmd])]);
        var result = CommandDispatcher.DispatchCompound(compound, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("I28R", result.Message);
        Assert.Contains("28R", result.Message);
    }

    // --- Error cases ---

    [Fact]
    public void Capp_UnknownApproach_Fails()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("VOR99", null, false, null, null, null, null, null, null, null, null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("Unknown approach", result.Message);
    }

    // --- Helpers ---

    private static PhaseContext MakeContext(AircraftState aircraft)
    {
        var cat = AircraftCategorization.Categorize(aircraft.AircraftType);
        return new PhaseContext
        {
            Aircraft = aircraft,
            Targets = aircraft.Targets,
            Category = cat,
            DeltaSeconds = 1.0,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
        };
    }

    private static NavigationDatabase MakeNavDb()
    {
        var procedure = new CifpApproachProcedure(
            "OAK",
            "I28R",
            'I',
            "ILS",
            "28R",
            [
                new CifpLeg("GROVE", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                new CifpLeg("FITKI", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 20, null, null, null),
                new CifpLeg("BERYL", CifpPathTerminator.TF, null, null, null, CifpFixRole.FAF, 30, null, null, null),
            ],
            new Dictionary<string, CifpTransition>(),
            [],
            false,
            null
        );

        return TestNavDbFactory.WithFixesRunwayAndApproaches(
            [("GROVE", 37.78, -122.35), ("FITKI", 37.76, -122.30), ("BERYL", 37.74, -122.26)],
            MakeRunway(),
            [procedure]
        );
    }

    private static NavigationDatabase MakeNavDbWithHoldInLieu()
    {
        var holdLeg = new CifpLeg("FITKI", CifpPathTerminator.HF, 'R', null, null, CifpFixRole.IF, 20, null, null, null);

        var procedure = new CifpApproachProcedure(
            "OAK",
            "I28R",
            'I',
            "ILS",
            "28R",
            [
                new CifpLeg("GROVE", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                holdLeg,
                new CifpLeg("BERYL", CifpPathTerminator.TF, null, null, null, CifpFixRole.FAF, 30, null, null, null),
            ],
            new Dictionary<string, CifpTransition>(),
            [],
            true,
            holdLeg
        );

        return TestNavDbFactory.WithFixesRunwayAndApproaches(
            [("GROVE", 37.78, -122.35), ("FITKI", 37.76, -122.30), ("BERYL", 37.74, -122.26)],
            MakeRunway(),
            [procedure]
        );
    }

    // --- AssignedValue audit ---

    [Fact]
    public void Capp_WithCrossFixAltitude_SetsAssignedAltitude()
    {
        var aircraft = MakeAircraft(altitude: 5000);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, "SUNOL", 37.5, -121.8, 3400, CrossFixAltitudeType.At);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Equal(3400, aircraft.Targets.AssignedAltitude);
    }

    [Fact]
    public void Capp_WithAssignedHeadingAndCrossFixAlt_SetsAssignedAltitude()
    {
        var aircraft = MakeAircraft(altitude: 5000);
        aircraft.Targets.TargetTrueHeading = new TrueHeading(340);
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(340);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ClearedApproachCommand("ILS28R", null, false, null, null, null, null, null, null, 3000, CrossFixAltitudeType.At);
        CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.Equal(3000, aircraft.Targets.AssignedAltitude);
    }

    [Fact]
    public void Jfac_ClearsAssignedSpeedAndAssignedHeading()
    {
        var aircraft = MakeAircraft();
        aircraft.Targets.TargetSpeed = 210;
        aircraft.Targets.AssignedSpeed = 210;
        aircraft.Targets.AssignedMagneticHeading = new MagneticHeading(340);
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new JoinFinalApproachCourseCommand("ILS28R");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Null(aircraft.Targets.AssignedSpeed);
        Assert.Null(aircraft.Targets.AssignedMagneticHeading);
    }

    // --- HoldingPatternPhase command acceptance ---

    [Theory]
    [InlineData(CanonicalCommandType.ClimbMaintain)]
    [InlineData(CanonicalCommandType.DescendMaintain)]
    [InlineData(CanonicalCommandType.Speed)]
    [InlineData(CanonicalCommandType.Mach)]
    public void HoldingPattern_AllowsAltitudeAndSpeedCommands(CanonicalCommandType cmd)
    {
        var phase = new HoldingPatternPhase
        {
            FixName = "EDDYY",
            FixLat = 37.5,
            FixLon = -122.5,
            InboundCourse = 180,
            LegLength = 1.0,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
        };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    [Theory]
    [InlineData(CanonicalCommandType.FlyHeading)]
    [InlineData(CanonicalCommandType.DirectTo)]
    [InlineData(CanonicalCommandType.ClearedToLand)]
    public void HoldingPattern_OtherCommands_ClearPhase(CanonicalCommandType cmd)
    {
        var phase = new HoldingPatternPhase
        {
            FixName = "EDDYY",
            FixLat = 37.5,
            FixLon = -122.5,
            InboundCourse = 180,
            LegLength = 1.0,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
        };
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(cmd));
    }

    private static NavigationDatabase MakeNavDbRunwayAndApproachOnly()
    {
        var procedure = new CifpApproachProcedure("OAK", "I28R", 'I', "ILS", "28R", [], new Dictionary<string, CifpTransition>(), [], false, null);

        return TestNavDbFactory.WithRunwayAndApproaches(MakeRunway(), [procedure]);
    }
}
