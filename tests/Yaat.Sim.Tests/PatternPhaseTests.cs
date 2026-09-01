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
        return PatternGeometry.Compute(rwy, AircraftCategory.Jet, "", 0, dir, null, null, null, authoredRunway: null);
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
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = onGround,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
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

        Assert.Equal(wp.UpwindHeading, ac.Targets.TargetTrueHeading);
        Assert.Equal(wp.PatternAltitude, ac.Targets.TargetAltitude);
        Assert.True(ac.Targets.DesiredVerticalRate > 0);
    }

    [Fact]
    public void Upwind_CompletesWhenPastDepartureEndAtPatternAltitude()
    {
        var wp = DefaultWaypoints();
        // Just past the crosswind turn point (the departure end) along the upwind heading, at pattern
        // altitude — AIM 4-3-2 commences the crosswind turn beyond the DER within 300 ft of TPA.
        var past = GeoMath.ProjectPoint(wp.CrosswindTurnLat, wp.CrosswindTurnLon, wp.UpwindHeading, 0.1);
        var ac = MakeAircraft(lat: past.Lat, lon: past.Lon, altitude: wp.PatternAltitude);
        var phase = new UpwindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.True(phase.OnTick(ctx));
    }

    [Fact]
    public void Upwind_BeforePassingDepartureEnd_DoesNotComplete()
    {
        var wp = DefaultWaypoints();
        // Short of the departure end (still over the runway) at pattern altitude: must not turn yet.
        var beforeDer = GeoMath.ProjectPoint(wp.CrosswindTurnLat, wp.CrosswindTurnLon, wp.UpwindHeading.ToReciprocal(), 0.2);
        var ac = MakeAircraft(lat: beforeDer.Lat, lon: beforeDer.Lon, altitude: wp.PatternAltitude);
        var phase = new UpwindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.False(phase.OnTick(ctx));
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

        Assert.Equal(wp.CrosswindHeading, ac.Targets.TargetTrueHeading);
        Assert.Null(ac.Targets.PreferredTurnDirection);
    }

    [Fact]
    public void Crosswind_RightPattern_SetsTurnRight()
    {
        var wp = DefaultWaypoints(PatternDirection.Right);
        var ac = MakeAircraft();
        var phase = new CrosswindPhase { Waypoints = wp };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Null(ac.Targets.PreferredTurnDirection);
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

        Assert.Equal(wp.DownwindHeading, ac.Targets.TargetTrueHeading);
        Assert.Equal(wp.PatternAltitude, ac.Targets.TargetAltitude);
        Assert.Null(ac.Targets.PreferredTurnDirection);
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

    [Theory]
    [InlineData(CanonicalCommandType.Speed)]
    [InlineData(CanonicalCommandType.ReduceToFinalApproachSpeed)]
    [InlineData(CanonicalCommandType.ResumeNormalSpeed)]
    [InlineData(CanonicalCommandType.DeleteSpeedRestrictions)]
    public void Downwind_AcceptsSpeedCommandsWithoutClearingPhase(CanonicalCommandType cmd)
    {
        // Pattern phases declare ManagesSpeed=true and own the speed schedule;
        // a controller speed assignment must not tear down the lateral phase.
        var phase = new DownwindPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
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

        Assert.Equal(wp.BaseHeading, ac.Targets.TargetTrueHeading);
        Assert.Null(ac.Targets.PreferredTurnDirection);
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
    public void Base_AcceptsClearedToLand()
    {
        var phase = new BasePhase();

        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.ClearedToLand));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.GoAround));
    }

    [Theory]
    [InlineData(CanonicalCommandType.Speed)]
    [InlineData(CanonicalCommandType.ReduceToFinalApproachSpeed)]
    [InlineData(CanonicalCommandType.ResumeNormalSpeed)]
    [InlineData(CanonicalCommandType.DeleteSpeedRestrictions)]
    public void Base_AcceptsSpeedCommandsWithoutClearingPhase(CanonicalCommandType cmd)
    {
        var phase = new BasePhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    [Theory]
    [InlineData(CanonicalCommandType.Speed)]
    [InlineData(CanonicalCommandType.ReduceToFinalApproachSpeed)]
    [InlineData(CanonicalCommandType.ResumeNormalSpeed)]
    [InlineData(CanonicalCommandType.DeleteSpeedRestrictions)]
    public void Upwind_AcceptsSpeedCommandsWithoutClearingPhase(CanonicalCommandType cmd)
    {
        var phase = new UpwindPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    [Theory]
    [InlineData(CanonicalCommandType.Speed)]
    [InlineData(CanonicalCommandType.ReduceToFinalApproachSpeed)]
    [InlineData(CanonicalCommandType.ResumeNormalSpeed)]
    [InlineData(CanonicalCommandType.DeleteSpeedRestrictions)]
    public void Crosswind_AcceptsSpeedCommandsWithoutClearingPhase(CanonicalCommandType cmd)
    {
        var phase = new CrosswindPhase();
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    [Theory]
    [InlineData(CanonicalCommandType.Speed)]
    [InlineData(CanonicalCommandType.ReduceToFinalApproachSpeed)]
    [InlineData(CanonicalCommandType.ResumeNormalSpeed)]
    [InlineData(CanonicalCommandType.DeleteSpeedRestrictions)]
    public void PatternEntry_AcceptsSpeedCommandsWithoutClearingPhase(CanonicalCommandType cmd)
    {
        var phase = new PatternEntryPhase
        {
            EntryLat = 37.0,
            EntryLon = -122.0,
            PatternAltitude = 1100,
            Kind = PatternEntryKind.FortyFive,
        };
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(cmd));
    }

    /// <summary>
    /// A Base entry hands straight to <see cref="BasePhase"/>, and a <em>close-in</em> Final entry to
    /// <see cref="Phases.Tower.FinalApproachPhase"/> with no room to decelerate — the #292 low-approach
    /// runway retarget builds one half a mile from the threshold. Both must join at the speed of the leg
    /// rather than accelerate to pattern speed first.
    /// </summary>
    [Theory]
    [InlineData(PatternEntryKind.Final)]
    [InlineData(PatternEntryKind.Base)]
    public void PatternEntry_OnStart_TargetsTheSpeedOfTheLegBeingJoined(PatternEntryKind kind)
    {
        TestVnasData.EnsureInitialized();

        var rwy = DefaultRunway();
        var ac = MakeAircraft(ias: 200);
        // Entry point 0.5 nm off the threshold — the close-in join the #292 retarget produces.
        var entry = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), 0.5);
        var phase = new PatternEntryPhase
        {
            EntryLat = entry.Lat,
            EntryLon = entry.Lon,
            PatternAltitude = 1100,
            Kind = kind,
        };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        double downwind = AircraftPerformance.DownwindSpeed(ac.AircraftType, AircraftCategory.Jet);
        double expected =
            kind == PatternEntryKind.Final
                ? AircraftPerformance.ApproachSpeed(ac.AircraftType, AircraftCategory.Jet)
                : AircraftPerformance.BaseSpeed(ac.AircraftType, AircraftCategory.Jet);

        Assert.True(expected < downwind, $"test premise: {kind} speed {expected:F0} should be below downwind speed {downwind:F0}");
        Assert.Equal(expected, ac.Targets.TargetSpeed);
    }

    /// <summary>
    /// The default Final entry point is the glideslope/TPA intercept — about 4.7 nm out for a jet, and
    /// <c>EF FINAL &lt;dist&gt;</c> can place it further. Commanding Vref there would fly the whole
    /// straight-in slow, below the 170/210 kt floors 7110.65 §5-7-3.c.1.b sets for an arriving turbojet,
    /// and would defeat FinalApproachPhase's staged 1.3·Vref → Vref profile. A distant Final entry joins
    /// at pattern speed and lets that phase own the deceleration.
    /// </summary>
    [Fact]
    public void PatternEntry_OnStart_DistantFinalEntry_JoinsAtPatternSpeed()
    {
        TestVnasData.EnsureInitialized();

        var rwy = DefaultRunway();
        var ac = MakeAircraft(ias: 250);
        var entry = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), 4.7);
        var phase = new PatternEntryPhase
        {
            EntryLat = entry.Lat,
            EntryLon = entry.Lon,
            PatternAltitude = 1500,
            Kind = PatternEntryKind.Final,
        };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Equal(AircraftPerformance.DownwindSpeed(ac.AircraftType, AircraftCategory.Jet), ac.Targets.TargetSpeed);
    }

    [Fact]
    public void PatternEntry_OnStart_DownwindKindsStillTargetPatternSpeed()
    {
        TestVnasData.EnsureInitialized();

        var ac = MakeAircraft(ias: 250);
        var phase = new PatternEntryPhase
        {
            EntryLat = 37.05,
            EntryLon = -122.0,
            PatternAltitude = 1100,
            Kind = PatternEntryKind.FortyFive,
        };
        var ctx = Ctx(ac);

        phase.OnStart(ctx);

        Assert.Equal(AircraftPerformance.DownwindSpeed(ac.AircraftType, AircraftCategory.Jet), ac.Targets.TargetSpeed);
    }

    /// <summary>
    /// A controller speed assignment survives the leg transitions of the pattern. Per 7110.65 §5-7-4 it
    /// is the controller who terminates a speed adjustment ("RESUME NORMAL SPEED"); the aircraft does not
    /// revert on its own on reaching the next leg. <see cref="Phases.Tower.FinalApproachPhase"/> already
    /// respects <c>HasExplicitSpeedCommand</c> — the pattern legs were the outlier.
    /// </summary>
    [Theory]
    [InlineData("upwind")]
    [InlineData("crosswind")] // correct by omission — CrosswindPhase.OnStart writes no speed; guards against one being added
    [InlineData("downwind")]
    [InlineData("base")]
    [InlineData("entry")]
    [InlineData("midfield")]
    [InlineData("teardrop")]
    public void PatternLeg_OnStart_KeepsAnExplicitControllerSpeed(string leg)
    {
        TestVnasData.EnsureInitialized();

        var wp = DefaultWaypoints();
        var ac = MakeAircraft(altitude: wp.PatternAltitude);
        const double AssignedSpeed = 180.0;
        ac.Targets.TargetSpeed = AssignedSpeed;
        ac.Targets.HasExplicitSpeedCommand = true;

        Phase phase = leg switch
        {
            "upwind" => new UpwindPhase { Waypoints = wp },
            "crosswind" => new CrosswindPhase { Waypoints = wp },
            "downwind" => new DownwindPhase { Waypoints = wp },
            "base" => new BasePhase { Waypoints = wp },
            "midfield" => new MidfieldCrossingPhase { Waypoints = wp },
            "teardrop" => new TeardropReentryPhase { Waypoints = wp },
            _ => new PatternEntryPhase
            {
                EntryLat = 37.05,
                EntryLon = -122.0,
                PatternAltitude = wp.PatternAltitude,
                Kind = PatternEntryKind.FortyFive,
            },
        };

        phase.OnStart(Ctx(ac));

        Assert.Equal(AssignedSpeed, ac.Targets.TargetSpeed);
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
        Assert.NotNull(ac.Targets.TargetTrueHeading);
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

    /// <summary>
    /// A lateral command still cancels the crossing — it re-routes the aircraft, so the rest of the circuit no longer
    /// applies. A speed or altitude adjustment does not: it is additive, exactly as on the pattern legs either side.
    /// This phase carries the remainder of the circuit in its phase list, so clearing it on a plain <c>SPD</c> threw
    /// away Downwind → Base → FinalApproach → Landing and left the aircraft with no phases at all.
    /// </summary>
    [Fact]
    public void MidfieldCrossing_LateralCommandClearsPhase_SpeedIsAdditive()
    {
        var phase = new MidfieldCrossingPhase();
        Assert.Equal(CommandAcceptance.ClearsPhase, phase.CanAcceptCommand(CanonicalCommandType.FlyHeading));
        Assert.Equal(CommandAcceptance.Allowed, phase.CanAcceptCommand(CanonicalCommandType.Speed));
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
        Assert.Equal(expected, wp.CrosswindHeading.Degrees, precision: 1);
    }

    [Fact]
    public void PatternGeometry_RightPattern_CrosswindIs90Right()
    {
        var wp = DefaultWaypoints(PatternDirection.Right);

        // Runway heading 280, right crosswind = 280 + 90 = 370 → 10
        double expected = (280.0 + 90.0) % 360.0;
        Assert.Equal(expected, wp.CrosswindHeading.Degrees, precision: 1);
    }

    [Fact]
    public void PatternGeometry_DownwindIsReciprocal()
    {
        var wp = DefaultWaypoints();

        double expected = (280.0 + 180.0) % 360.0;
        Assert.Equal(expected, wp.DownwindHeading.Degrees, precision: 1);
    }

    [Fact]
    public void PatternGeometry_PatternAltitude_IsFieldPlusAgl()
    {
        var rwy = DefaultRunway(100);
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, "", 0, PatternDirection.Left, null, null, null, authoredRunway: null);

        double expectedAgl = CategoryPerformance.PatternAltitudeAgl(AircraftCategory.Jet);
        Assert.Equal(100.0 + expectedAgl, wp.PatternAltitude, precision: 0);
    }
}
