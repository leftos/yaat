using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Go-around climb-out targeting and pattern-intent resolution (issue #283).
///
/// A pattern go-around levels 300 ft below pattern altitude so <see cref="UpwindPhase"/> can release
/// the crosswind turn immediately (AIM 4-3-2). Which pattern the aircraft rejoins, and how high it
/// climbs to get there, is resolved by <see cref="GoAroundHelper"/> — shared by the automatic
/// go-around and the <c>GA</c> command so both behave identically.
/// </summary>
public class GoAroundClimbOutTests
{
    public GoAroundClimbOutTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private const double PistonPatternAltitudeAgl = 1000.0;
    private const double HandoffMarginFt = 300.0;

    /// <summary>OAK 28L authors a 600 ft AGL pattern in its ground layout (the North Field GA pattern).</summary>
    private const double Oak28LAuthoredPatternAltitudeAgl = 600.0;

    private static RunwayInfo? Runway(string designator) => NavigationDatabase.Instance.GetRunway("OAK", designator);

    private static AircraftState MakeVfrAircraft(RunwayInfo runway)
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            AirportId = "OAK",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 300,
            IndicatedAirspeed = 80,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { FlightRules = "VFR", Destination = "KOAK" },
        };
        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        return aircraft;
    }

    private static PatternWaypoints WaypointsFor(RunwayInfo runway, PatternDirection direction) =>
        PatternGeometry.Compute(runway, AircraftCategory.Piston, direction, sizeOverrideNm: null, altitudeOverrideFt: null, airportRunways: null);

    // ---- ResolvePatternIntent -------------------------------------------------------------

    /// <summary>
    /// The issue #283 state: ERD stamped a right pattern, then CLAND dropped both direction fields to
    /// signal full-stop intent. The completed downwind leg still names the side the controller
    /// assigned, and it must win over the runway-suffix default (28L would otherwise infer Left).
    /// </summary>
    [Fact]
    public void ResolvePatternIntent_AfterClandWipedDirection_RecoversSideFromFlownPatternLeg()
    {
        var runway = Runway("28L");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Phases!.Add(new DownwindPhase { Waypoints = WaypointsFor(runway, PatternDirection.Right), Status = PhaseStatus.Completed });
        aircraft.Phases.Add(new FinalApproachPhase());

        Assert.Null(aircraft.Phases.TrafficDirection);
        Assert.Equal(PatternDirection.Right, GoAroundHelper.ResolvePatternIntent(aircraft));
    }

    /// <summary>A VFR aircraft that never flew a pattern falls back to the outboard-pattern parallel-runway convention.</summary>
    [Fact]
    public void ResolvePatternIntent_VfrWithNoPatternLegs_InfersFromParallelRunwaySuffix()
    {
        var right = Runway("28R");
        var left = Runway("28L");
        if (right is null || left is null)
        {
            return;
        }

        Assert.Equal(PatternDirection.Right, GoAroundHelper.ResolvePatternIntent(MakeVfrAircraft(right)));
        Assert.Equal(PatternDirection.Left, GoAroundHelper.ResolvePatternIntent(MakeVfrAircraft(left)));
    }

    /// <summary>The controller's persistent MLT/MRT intent outranks anything inferred.</summary>
    [Fact]
    public void ResolvePatternIntent_PersistentMltIntent_OutranksRunwaySuffix()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Pattern.TrafficDirection = PatternDirection.Left;

        Assert.Equal(PatternDirection.Left, GoAroundHelper.ResolvePatternIntent(aircraft));
    }

    /// <summary>IFR traffic not already in a pattern flies the missed approach, not a circuit.</summary>
    [Fact]
    public void ResolvePatternIntent_IfrNotInPattern_ReturnsNull()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.FlightPlan = new AircraftFlightPlan { FlightRules = "IFR", Destination = "KOAK" };

        Assert.Null(GoAroundHelper.ResolvePatternIntent(aircraft));
    }

    // ---- ResolveClimbOutAltitude ----------------------------------------------------------

    [Fact]
    public void ResolveClimbOutAltitude_CategoryDefault_LevelsThreeHundredBelowPatternAltitude()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null);

        int expected = (int)(runway.ElevationFt + PistonPatternAltitudeAgl - HandoffMarginFt);
        Assert.Equal(expected, GoAroundHelper.ResolveClimbOutAltitude(ctx, isPattern: true, missedApproachPhases: []));
    }

    /// <summary>
    /// OAK 28L's authored 600 ft AGL pattern must drive the climb-out, not the 1000 ft AGL piston
    /// default — otherwise the go-around levels 100 ft above the pattern it is rejoining.
    /// </summary>
    [Fact]
    public void ResolveClimbOutAltitude_AuthoredPatternAltitude_OverridesCategoryDefault()
    {
        var runway = Runway("28L");
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (runway is null || layout is null || layout.FindRunway("28L")?.PatternAltitudeAglFt is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);

        int expected = (int)(runway.ElevationFt + Oak28LAuthoredPatternAltitudeAgl - HandoffMarginFt);
        Assert.Equal(expected, GoAroundHelper.ResolveClimbOutAltitude(ctx, isPattern: true, missedApproachPhases: []));
    }

    /// <summary>A commanded pattern altitude (<c>MRT 28R 15</c>) is MSL and beats the authored value.</summary>
    [Fact]
    public void ResolveClimbOutAltitude_CommandedAltitudeOverride_Wins()
    {
        var runway = Runway("28L");
        var layout = new TestAirportGroundData().GetLayout("OAK");
        if (runway is null || layout is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Pattern.AltitudeOverrideFt = 1500;
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, layout);

        Assert.Equal(1200, GoAroundHelper.ResolveClimbOutAltitude(ctx, isPattern: true, missedApproachPhases: []));
    }

    /// <summary>
    /// Without an assigned runway the climb-out must still resolve. Nullable arithmetic used to
    /// collapse the whole expression to null, silently reverting the aircraft to a 2000 ft AGL climb.
    /// </summary>
    [Fact]
    public void ResolveClimbOutAltitude_NoAssignedRunway_FallsBackToFieldElevation()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Phases!.AssignedRunway = null;
        var ctx = CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null);

        int expected = (int)(ctx.FieldElevation + PistonPatternAltitudeAgl - HandoffMarginFt);
        Assert.Equal(expected, GoAroundHelper.ResolveClimbOutAltitude(ctx, isPattern: true, missedApproachPhases: []));
    }

    /// <summary>A non-pattern go-around still self-clears at 2000 ft AGL (null target).</summary>
    [Fact]
    public void ResolveClimbOutAltitude_NotPattern_ReturnsNull()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var ctx = CommandDispatcher.BuildMinimalContext(MakeVfrAircraft(runway), groundLayout: null);

        Assert.Null(GoAroundHelper.ResolveClimbOutAltitude(ctx, isPattern: false, missedApproachPhases: []));
    }

    // ---- The GA command ---------------------------------------------------------------------

    /// <summary>
    /// Issue #283: a bare <c>GA</c> to a VFR arrival re-enters the pattern and climbs to pattern
    /// altitude − 300, matching the automatic go-around. It used to run straight out to 2000 ft AGL.
    /// </summary>
    [Fact]
    public void GoAroundCommand_VfrArrival_ReentersPatternAtPatternAltitude()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Phases!.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null));

        var result = PatternCommandHandler.TryGoAround(new GoAroundCommand(null, null, null), aircraft, groundLayout: null);

        Assert.True(result.Success, result.Message);
        var goAround = Assert.IsType<GoAroundPhase>(aircraft.Phases.CurrentPhase);
        Assert.True(goAround.ReenterPattern);
        Assert.Equal((int)(runway.ElevationFt + PistonPatternAltitudeAgl - HandoffMarginFt), goAround.TargetAltitude);
    }

    /// <summary>
    /// <c>GA 270</c> hands the climb-out to the controller. A VFR aircraft that was not already
    /// established in a pattern must not infer one and turn back toward the field on its own.
    /// </summary>
    [Fact]
    public void GoAroundCommand_WithAssignedHeading_DoesNotInferPatternReentry()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Phases!.Add(new FinalApproachPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null));

        var result = PatternCommandHandler.TryGoAround(new GoAroundCommand(new MagneticHeading(270), null, null), aircraft, groundLayout: null);

        Assert.True(result.Success, result.Message);
        var goAround = Assert.IsType<GoAroundPhase>(aircraft.Phases.CurrentPhase);
        Assert.False(goAround.ReenterPattern);
        Assert.Null(goAround.TargetAltitude);
        Assert.Null(aircraft.Phases.TrafficDirection);
    }

    // ---- Option clearances share the same inference -----------------------------------------

    /// <summary>
    /// <c>CTL</c> with no stated side puts the aircraft into pattern mode, resolving the side exactly
    /// as a go-around does. On 28R that is right traffic (outboard of the 28L parallel), not the
    /// blanket left-traffic default the option clearances used to apply.
    /// </summary>
    [Fact]
    public void ClearedTouchAndGo_WithoutStatedSide_InfersSameDirectionAsGoAround()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Phases!.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null));

        var result = PatternCommandHandler.TrySetupTouchAndGo(aircraft, trafficPattern: null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(PatternDirection.Right, aircraft.Phases.TrafficDirection);
    }

    /// <summary>A side the aircraft has already flown outranks the runway-side convention.</summary>
    [Fact]
    public void ClearedTouchAndGo_WithoutStatedSide_KeepsTheSideAlreadyFlown()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Phases!.Add(new DownwindPhase { Waypoints = WaypointsFor(runway, PatternDirection.Left) });
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null));

        var result = PatternCommandHandler.TrySetupTouchAndGo(aircraft, trafficPattern: null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(PatternDirection.Left, aircraft.Phases.TrafficDirection);
    }

    // ---- Cross-runway MLT/MRT ---------------------------------------------------------------

    /// <summary>
    /// A landing clearance names a runway (7110.65 §3-10-5). Sending the aircraft around a pattern for
    /// a different runway invalidates it — <c>FinalApproachPhase.HasLandingClearance</c> reads only the
    /// clearance type, so a surviving clearance would let the aircraft land on a runway the controller
    /// never cleared it for, with no "no landing clearance" warning.
    /// </summary>
    [Fact]
    public void MakeRightTraffic_ForADifferentRunway_InvalidatesTheLandingClearance()
    {
        var runway = Runway("28R");
        if (runway is null || Runway("30") is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Phases!.Add(new DownwindPhase { Waypoints = WaypointsFor(runway, PatternDirection.Right) });
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null));
        aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
        aircraft.Phases.ClearedRunwayId = "28R";

        var result = PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, runwayId: "30", altitudeOverride: null);

        Assert.True(result.Success, result.Message);
        Assert.Equal("30", aircraft.Phases.AssignedRunway?.Designator);
        Assert.Null(aircraft.Phases.LandingClearance);
        Assert.Null(aircraft.Phases.ClearedRunwayId);
    }

    /// <summary>A same-runway MLT/MRT only changes the pattern side, so the landing clearance stands.</summary>
    [Fact]
    public void MakeRightTraffic_ForTheSameRunway_KeepsTheLandingClearance()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Phases!.Add(new DownwindPhase { Waypoints = WaypointsFor(runway, PatternDirection.Left) });
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null));
        aircraft.Phases.LandingClearance = ClearanceType.ClearedToLand;
        aircraft.Phases.ClearedRunwayId = "28R";

        var result = PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, runwayId: "28R", altitudeOverride: null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(ClearanceType.ClearedToLand, aircraft.Phases.LandingClearance);
        Assert.Equal("28R", aircraft.Phases.ClearedRunwayId);
    }

    // ---- MLT/MRT during an active go-around ------------------------------------------------

    /// <summary>
    /// MRT issued while the aircraft is climbing out on a non-pattern go-around retargets the active
    /// phase to pattern altitude − 300 and leaves it last in the list, so PhaseRunner's auto-cycle
    /// appends the circuit when the climb completes.
    /// </summary>
    [Fact]
    public void MakeRightTraffic_DuringNonPatternGoAround_RetargetsClimbOutToPatternAltitude()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Phases!.Add(
            new GoAroundPhase
            {
                ReenterPattern = false,
                TargetAltitude = null,
                NextLandingFullStop = true,
            }
        );
        aircraft.Phases.Add(new FinalApproachPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null));

        var goAround = Assert.IsType<GoAroundPhase>(aircraft.Phases.CurrentPhase);
        Assert.Equal(runway.ElevationFt + 2000, aircraft.Targets.TargetAltitude);

        var result = PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, runwayId: null, altitudeOverride: null);

        Assert.True(result.Success, result.Message);
        Assert.True(goAround.ReenterPattern);
        Assert.Equal((int)(runway.ElevationFt + PistonPatternAltitudeAgl - HandoffMarginFt), goAround.TargetAltitude);
        Assert.Equal(goAround.TargetAltitude, aircraft.Targets.TargetAltitude);
        Assert.Equal(PatternDirection.Right, aircraft.Phases.TrafficDirection);

        // The go-around is now the last phase, so PhaseRunner sees an empty list once it advances
        // past the climb-out and appends the next circuit there.
        Assert.Same(goAround, aircraft.Phases.Phases[^1]);
        Assert.Same(goAround, aircraft.Phases.CurrentPhase);
    }

    /// <summary>MRT drops a heading assigned by <c>GA 270</c>: the pattern, not a vector, owns the aircraft.</summary>
    [Fact]
    public void MakeRightTraffic_DuringGoAroundWithAssignedHeading_DropsTheHeading()
    {
        var runway = Runway("28R");
        if (runway is null)
        {
            return;
        }

        var aircraft = MakeVfrAircraft(runway);
        aircraft.Phases!.Add(
            new GoAroundPhase
            {
                AssignedMagneticHeading = new MagneticHeading(270),
                ReenterPattern = false,
                NextLandingFullStop = true,
            }
        );
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, groundLayout: null));

        var goAround = Assert.IsType<GoAroundPhase>(aircraft.Phases.CurrentPhase);

        PatternCommandHandler.TryChangePatternDirection(aircraft, PatternDirection.Right, runwayId: null, altitudeOverride: null);

        Assert.Null(goAround.AssignedMagneticHeading);
        Assert.Equal(runway.TrueHeading.Degrees, aircraft.Targets.TargetTrueHeading?.Degrees ?? double.NaN, 3);
    }
}
