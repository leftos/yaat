using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;

namespace Yaat.Sim.Tests;

public class PatternPhaseTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RunwayInfo DefaultRunway(double elevationFt = 100) =>
        TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: elevationFt);

    private static PatternWaypoints DefaultWaypoints(PatternDirection dir = PatternDirection.Left)
    {
        var rwy = DefaultRunway();
        return PatternGeometry.Compute(rwy, AircraftCategory.Jet, dir);
    }

    private static AircraftState MakeAircraft(
        double lat = 37.0,
        double lon = -122.0,
        double heading = 280,
        double altitude = 1100,
        double ias = 200,
        bool onGround = false
    )
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = heading,
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = onGround,
            Departure = "TEST",
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, double dt = 1.0)
    {
        var rwy = DefaultRunway();
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = NullLogger.Instance,
        };
    }

    // -------------------------------------------------------------------------
    // UpwindPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void Upwind_OnStart_SetsRunwayHeadingAndClimb()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(altitude: 200);
        var phase = new UpwindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Equal(wp.UpwindHeading, ac.Targets.TargetHeading);
        Assert.Equal(wp.PatternAltitude, ac.Targets.TargetAltitude);
        Assert.True(ac.Targets.DesiredVerticalRate > 0);
    }

    [Fact]
    public void Upwind_CompletesWhenReachingCrosswindTurnPoint()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(lat: wp.CrosswindTurnLat, lon: wp.CrosswindTurnLon);
        var phase = new UpwindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        // At the crosswind turn point (within 0.3nm)
        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void Upwind_Extended_NeverCompletes()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(lat: wp.CrosswindTurnLat, lon: wp.CrosswindTurnLon);
        var phase = new UpwindPhase { Waypoints = wp, IsExtended = true };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        // Even at the crosswind turn point, should not complete when extended
        Assert.False(phase.OnTick(ctx));
    }

    [Fact]
    public void Upwind_FarFromTurnPoint_DoesNotComplete()
    {
        var wp = DefaultWaypoints();
        // Aircraft far from turn point
        var ac = MakeAircraft(lat: 37.0, lon: -122.0);
        var phase = new UpwindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));
    }

    // -------------------------------------------------------------------------
    // CrosswindPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void Crosswind_OnStart_SetsCrosswindHeadingAndTurnDirection()
    {
        var wp = DefaultWaypoints(PatternDirection.Left);
        var ac = MakeAircraft();
        var phase = new CrosswindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Equal(wp.CrosswindHeading, ac.Targets.TargetHeading);
        Assert.Equal(TurnDirection.Left, ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void Crosswind_RightPattern_SetsTurnRight()
    {
        var wp = DefaultWaypoints(PatternDirection.Right);
        var ac = MakeAircraft();
        var phase = new CrosswindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Equal(TurnDirection.Right, ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void Crosswind_ContinuesClimbBelowPatternAlt()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(altitude: 500); // well below pattern alt
        var phase = new CrosswindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Equal(wp.PatternAltitude, ac.Targets.TargetAltitude);
        Assert.True(ac.Targets.DesiredVerticalRate > 0);
    }

    [Fact]
    public void Crosswind_CompletesAtDownwindStart()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(lat: wp.DownwindStartLat, lon: wp.DownwindStartLon);
        var phase = new CrosswindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void Crosswind_Extended_NeverCompletes()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(lat: wp.DownwindStartLat, lon: wp.DownwindStartLon);
        var phase = new CrosswindPhase { Waypoints = wp, IsExtended = true };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));
    }

    // -------------------------------------------------------------------------
    // DownwindPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void Downwind_OnStart_SetsDownwindHeadingAndPatternAlt()
    {
        var wp = DefaultWaypoints(PatternDirection.Left);
        var ac = MakeAircraft(altitude: wp.PatternAltitude);
        var phase = new DownwindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Equal(wp.DownwindHeading, ac.Targets.TargetHeading);
        Assert.Equal(wp.PatternAltitude, ac.Targets.TargetAltitude);
        Assert.Equal(TurnDirection.Left, ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void Downwind_CompletesAtBaseTurnPoint()
    {
        var wp = DefaultWaypoints();
        // Place aircraft at base turn point
        var ac = MakeAircraft(lat: wp.BaseTurnLat, lon: wp.BaseTurnLon, altitude: wp.PatternAltitude);
        var phase = new DownwindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void Downwind_Extended_NeverCompletes()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(lat: wp.BaseTurnLat, lon: wp.BaseTurnLon, altitude: wp.PatternAltitude);
        var phase = new DownwindPhase { Waypoints = wp, IsExtended = true };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));
    }

    [Fact]
    public void Downwind_AcceptsClearedToLandAndExtend()
    {
        var phase = new DownwindPhase();

        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClearedToLand));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClearedForOption));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.GoAround));
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
    }

    // -------------------------------------------------------------------------
    // BasePhase
    // -------------------------------------------------------------------------

    [Fact]
    public void Base_OnStart_SetsBaseHeadingAndDescent()
    {
        var wp = DefaultWaypoints(PatternDirection.Left);
        var ac = MakeAircraft(altitude: wp.PatternAltitude);
        var phase = new BasePhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Equal(wp.BaseHeading, ac.Targets.TargetHeading);
        Assert.Equal(TurnDirection.Left, ac.Targets.PreferredTurnDirection);
        Assert.True(ac.Targets.DesiredVerticalRate < 0); // descending
    }

    [Fact]
    public void Base_CompletesNearFinalApproachCourse()
    {
        var wp = DefaultWaypoints();
        // Place aircraft on the extended centerline (near threshold, cross-track ~0)
        var ac = MakeAircraft(lat: wp.ThresholdLat, lon: wp.ThresholdLon, altitude: wp.PatternAltitude);
        var phase = new BasePhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        // Cross-track distance to the final approach course should be < 0.3nm
        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void Base_Extended_NeverCompletes()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(lat: wp.ThresholdLat, lon: wp.ThresholdLon, altitude: wp.PatternAltitude);
        var phase = new BasePhase { Waypoints = wp, IsExtended = true };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));
    }

    [Fact]
    public void Base_AcceptsClearedToLand()
    {
        var phase = new BasePhase();

        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClearedToLand));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.GoAround));
    }

    // -------------------------------------------------------------------------
    // MidfieldCrossingPhase
    // -------------------------------------------------------------------------

    [Fact]
    public void MidfieldCrossing_OnStart_SetsHeadingTowardMidfieldAndHigherAlt()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(altitude: wp.PatternAltitude);
        var phase = new MidfieldCrossingPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        // Target altitude should be pattern + 500ft
        Assert.Equal(wp.PatternAltitude + 500, ac.Targets.TargetAltitude);
        Assert.NotNull(ac.Targets.TargetHeading);
    }

    [Fact]
    public void MidfieldCrossing_CompletesWhenNearMidfield()
    {
        var wp = DefaultWaypoints();
        // Midfield target is average of downwind start and downwind abeam
        double midLat = (wp.DownwindStartLat + wp.DownwindAbeamLat) / 2.0;
        double midLon = (wp.DownwindStartLon + wp.DownwindAbeamLon) / 2.0;

        var ac = MakeAircraft(lat: midLat, lon: midLon, altitude: wp.PatternAltitude + 500);
        var phase = new MidfieldCrossingPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        // Within 0.5nm arrival → should complete
        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void MidfieldCrossing_FarFromMidfield_DoesNotComplete()
    {
        var wp = DefaultWaypoints();
        var ac = MakeAircraft(lat: 37.0, lon: -122.0, altitude: wp.PatternAltitude + 500);
        var phase = new MidfieldCrossingPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));
    }

    [Fact]
    public void MidfieldCrossing_AnyCommand_ClearsPhase()
    {
        var phase = new MidfieldCrossingPhase();
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.Speed));
    }

    // -------------------------------------------------------------------------
    // PatternGeometry.Compute (cross-cutting)
    // -------------------------------------------------------------------------

    [Fact]
    public void PatternGeometry_LeftPattern_CrosswindIs90Left()
    {
        var wp = DefaultWaypoints(PatternDirection.Left);

        // Runway heading 280, left crosswind = 280 - 90 = 190
        double expected = (280.0 - 90.0 + 360.0) % 360.0;
        Assert.Equal(expected, wp.CrosswindHeading, precision: 1);
    }

    [Fact]
    public void PatternGeometry_RightPattern_CrosswindIs90Right()
    {
        var wp = DefaultWaypoints(PatternDirection.Right);

        // Runway heading 280, right crosswind = 280 + 90 = 370 → 10
        double expected = (280.0 + 90.0) % 360.0;
        Assert.Equal(expected, wp.CrosswindHeading, precision: 1);
    }

    [Fact]
    public void PatternGeometry_DownwindIsReciprocal()
    {
        var wp = DefaultWaypoints();

        double expected = (280.0 + 180.0) % 360.0;
        Assert.Equal(expected, wp.DownwindHeading, precision: 1);
    }

    [Fact]
    public void PatternGeometry_PatternAltitude_IsFieldPlusAgl()
    {
        var rwy = DefaultRunway(100);
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left);

        double expectedAgl = CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Jet);
        Assert.Equal(100.0 + expectedAgl, wp.PatternAltitude, precision: 0);
    }
}
