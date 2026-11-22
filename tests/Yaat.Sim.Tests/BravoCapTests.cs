using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// Solo training: an uncleared VFR aircraft flying a phase under the SFO Class B shelf (2,100 ft floor over Oakland) caps
/// its climb at 2,000 (<see cref="AircraftState.BravoCap"/>), as the free-flight level-off does (14 CFR 91.131; AIM 3-2-3.b).
/// A controller altitude through the shelf draws an unable once per command (AIM 5-5-6.a.3; AIM 4-4-1.b; 7110.65 §7-9-2
/// NOTE 1). In an RPO session nothing caps and nothing says unable.
/// </summary>
public sealed class BravoCapTests
{
    private const string Callsign = "N436MS";
    private const int Cap = 2000;
    private const double KoakFieldElevation = 9;

    // Climbing out of KOAK 28R under the SFO Class B shelf (Issue308AirspaceLevelOffTests).
    private static readonly LatLon UnderSfoShelf = new(37.7387, -122.2474);

    // Well east of every Bay Area Class B footprint.
    private static readonly LatLon ClearOfClassB = new(37.6258, -120.9544);

    private static readonly LatLon Ksfo = new(37.6213, -122.3790);

    public BravoCapTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void PatternEntryUnderTheShelf_TargetAbove_LevelsAtTheCap_NoTurnNoSpeedChange()
    {
        (SimulationEngine engine, AircraftState ac) = PatternEntryUnderTheShelf(solo: true, tick: false);
        ac.Targets.TargetAltitude = 3500;
        TrueHeading? heading = ac.Targets.TargetTrueHeading;
        TurnDirection? turn = ac.Targets.PreferredTurnDirection;
        double? speed = ac.Targets.TargetSpeed;
        Assert.Null(ac.BravoCap);

        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);

        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);
        Assert.Equal(Cap, ac.BravoCap?.CapFtMsl);
        Assert.Equal(heading?.Degrees, ac.Targets.TargetTrueHeading?.Degrees);
        Assert.Equal(turn, ac.Targets.PreferredTurnDirection);
        Assert.Equal(speed, ac.Targets.TargetSpeed);
        for (int i = 0; i < 120; i++)
        {
            FlightPhysics.Update(ac, 1.0);
            Assert.True(ac.Altitude <= Cap + 1, $"climbed to {ac.Altitude:F0} through the cap");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GoAroundUnderTheShelf_TheCapIsBackAtOnce_WithTheGoAroundTargetForItsRelease(bool soloContext)
    {
        // A controller's GA builds its context without the solo flag (CommandDispatcher.BuildMinimalContext).
        (SimulationEngine engine, AircraftState ac) = PatternEntryUnderTheShelf(solo: true, tick: true);
        BravoCap before = Assert.IsType<BravoCap>(ac.BravoCap);

        new GoAroundPhase { TargetAltitude = 3000 }.OnStart(Context(ac, soloContext, KoakFieldElevation));

        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);
        BravoCap after = Assert.IsType<BravoCap>(ac.BravoCap);
        Assert.Equal(before.VolumeId, after.VolumeId);
        Assert.Equal(3000, after.PriorTargetAltitude);
        Assert.Equal(0, StepCountingUnables(engine, ac, 3, "unable"));
    }

    [Fact]
    public void GoAroundClimbingTo3000UnderTheShelf_CompletesAtTheCap()
    {
        // "Go around, climb and maintain 3,000": the go-around's own target is above the 2,000 cap.
        (_, AircraftState ac) = PatternEntryUnderTheShelf(solo: true, tick: true);
        PhaseContext ctx = Context(ac, solo: false, KoakFieldElevation);
        var goAround = new GoAroundPhase { TargetAltitude = 3000 };
        goAround.OnStart(ctx);
        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);

        ac.Altitude = 1500;
        Assert.False(goAround.OnTick(ctx));
        ac.Altitude = Cap;
        Assert.True(goAround.OnTick(ctx), "the go-around completes level at the cap so the circuit continues");
    }

    [Fact]
    public void SelfClearGoAroundAtKoakUnderTheShelf_CompletesAtTheCap()
    {
        (_, AircraftState ac) = PatternEntryUnderTheShelf(solo: true, tick: true);
        PhaseContext ctx = Context(ac, solo: true, KoakFieldElevation);
        var goAround = new GoAroundPhase();
        goAround.OnStart(ctx);
        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);

        ac.Altitude = Cap;

        Assert.True(goAround.OnTick(ctx), "self-clear (2,009) is above the 2,000 cap; the go-around completes at the cap");
    }

    [Fact]
    public void VfrDepartureFromKoak_CappedInTheInitialClimb_StillCappedAfterItsPhases()
    {
        AircraftState ac = DepartureAtRunwayEnd("KOAK", "28R");
        (SimulationEngine engine, _) = Engine(ac, solo: true);

        engine.TickOneSecond();
        BravoCap cap = Assert.IsType<BravoCap>(ac.BravoCap);
        for (int i = 0; (i < 600) && (ac.Phases is { IsComplete: false }); i++)
        {
            engine.TickOneSecond();
            Assert.True(ac.Altitude <= cap.CapFtMsl + 50, $"climbed to {ac.Altitude:F0} through the {cap.CapFtMsl} cap");
        }

        Assert.True(ac.Phases is null or { IsComplete: true }, "the departure phases complete under the cap");
        TickSeconds(engine, 10);

        // The cap may have been lowered under a lower shelf on the way, never raised.
        BravoCap standing = Assert.IsType<BravoCap>(ac.BravoCap);
        Assert.True(standing.Ceiling <= cap.Ceiling, $"cap raised from {cap.Ceiling} to {standing.Ceiling}");
        Assert.Equal(standing.Ceiling, ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void CappedAircraftInFreeFlight_GetsNoLevelOffForItsOwnClassB()
    {
        // The ceiling is cleared under a standing record to isolate the guard: the projection then reaches the shelf.
        AircraftState ac = ClimbingUnderTheShelf();
        ac.StartBravoCap(new BravoCap("SFO", SfoVolumeId(), Cap, null, 3500));
        ac.Targets.AltitudeCeiling = null;
        Assert.NotNull(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, 60, AirspaceClass.Bravo));

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(solo: true), AirspaceDatabase.Default, LookupAirport, MinimalPhaseContext.For);

        Assert.Null(ac.Phases);
    }

    [Fact]
    public void CappedAircraftInFreeFlight_StillOrbitsOutsideTheCharlie()
    {
        AircraftState ac = Airborne(new LatLon(37.7213, -122.4200), trueHeading: 90, altitude: 2000);
        ac.IndicatedAirspeed = 600;
        ac.HasControllerAcknowledgedInitialContact = false;
        ac.StartBravoCap(new BravoCap("SFO", SfoVolumeId(), Cap, null, null));

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(solo: true), AirspaceDatabase.Default, LookupAirport, MinimalPhaseContext.For);

        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(Assert.Single(ac.Phases!.Phases));
        Assert.Equal(AirspaceClass.Charlie, hold.AirspaceClass);
    }

    [Fact]
    public void CappedAircraftInFreeFlight_StillOrbitsOutsideTheSurfaceArea()
    {
        LatLon edge = FirstPointOutside(Ksfo, 90, v => v.LowerFtMsl == 0);
        AircraftState ac = Airborne(GeoMath.ProjectPoint(edge, new TrueHeading(90), 0.5), trueHeading: 270, altitude: 1000);
        ac.HasControllerAcknowledgedInitialContact = true;
        ac.StartBravoCap(new BravoCap("SFO", SfoVolumeId(), Cap, null, null));

        PilotProactive.TickAirspaceBoundaryRespect(ac, SoloScenario(solo: true), AirspaceDatabase.Default, LookupAirport, MinimalPhaseContext.For);

        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(Assert.Single(ac.Phases!.Phases));
        Assert.Equal(AirspaceClass.Bravo, hold.AirspaceClass);
        Assert.Equal(AirspaceHoldMode.Orbit, hold.Mode);
    }

    [Fact]
    public void StandingCap_ReachingALowerShelfOfTheSameClassB_IsLowered()
    {
        (LatLon from, double heading, AirspaceVolume upper, AirspaceVolume lower) = FindStepDown();
        AircraftState ac = InAPhase(from, heading, lower.LowerFtMsl - 500);
        SimScenarioState scenario = SoloScenario(solo: true);

        PilotProactive.TickBravoCap(ac, scenario, AirspaceDatabase.Default);
        BravoCap first = Assert.IsType<BravoCap>(ac.BravoCap);
        Assert.Equal(upper.Id, first.VolumeId);
        PilotProactive.TickBravoCap(ac, scenario, AirspaceDatabase.Default);

        BravoCap lowered = Assert.IsType<BravoCap>(ac.BravoCap);
        Assert.Equal(lower.Id, lowered.VolumeId);
        Assert.True(lowered.CapFtMsl < first.CapFtMsl, $"cap {lowered.CapFtMsl} not below {first.CapFtMsl}");
        Assert.True(lowered.CapFtMsl < lower.LowerFtMsl);
        Assert.Equal(lowered.Ceiling, ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void CourseSwingUnderTheCapsOwnShelf_LeavesTheCapUnchanged()
    {
        // Eastbound under the 4,000 ring the level-off is 3,500; westbound it would be 2,500. A swing is no lower shelf.
        LatLon under = SfoGrid().First(p => StaysUnderFloor(p, 4000));
        AircraftState ac = InAPhase(under, 90, 2000);
        SimScenarioState scenario = SoloScenario(solo: true);
        PilotProactive.TickBravoCap(ac, scenario, AirspaceDatabase.Default);
        BravoCap eastbound = Assert.IsType<BravoCap>(ac.BravoCap);

        Steer(ac, under, 270);
        PilotProactive.TickBravoCap(ac, scenario, AirspaceDatabase.Default);

        Assert.Same(eastbound, ac.BravoCap);
        Assert.Equal(eastbound.Ceiling, ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void LowApproachClimbOutAtKoak_CappedBelowSelfClear_CompletesAtTheCap()
    {
        // No satellite in the fixture sits under a shelf low enough (KSQL is under 3,000, KOAK under 2,100), so the cap
        // record stands under the KOAK shelf with a controller's "at or below 1,200" beneath it: ceiling 1,200 < 1,509.
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("KOAK", "28R")!;
        double field = runway.AirportElevationFt;
        AircraftState ac = InAPhase(UnderSfoShelf, runway.TrueHeading.Degrees, field + 500);
        ac.Targets.AltitudeCeiling = 1200;
        PilotProactive.TickBravoCap(ac, SoloScenario(solo: true), AirspaceDatabase.Default);
        BravoCap cap = Assert.IsType<BravoCap>(ac.BravoCap);
        Assert.True(cap.Ceiling < field + 1500, $"premise: the cap {cap.Ceiling} is below the 1,500 ft AGL self-clear");
        var lowApproach = LowApproachPhase.FromSnapshot(
            new LowApproachPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 30,
                FieldElevation = field,
                RunwayHeadingDeg = runway.TrueHeading.Degrees,
                ClimbingOut = true,
            }
        );
        PhaseContext ctx = Context(ac, solo: true, field);

        ac.Altitude = cap.Ceiling - 300;
        Assert.False(lowApproach.OnTick(ctx));
        ac.Altitude = cap.Ceiling;

        Assert.True(lowApproach.OnTick(ctx), "the climb-out completes level at the cap so the next circuit is built");
    }

    [Fact]
    public void PendingApproachActivatingOverAFreeFlightLevelOff_TheCapTakesItOver_ClbrvReleasesFully()
    {
        (SimulationEngine engine, AircraftState ac) = LevelOffHoldUnderTheShelf();
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway("KOAK", "28R")!;
        var pending = new PendingApproachInfo
        {
            Clearance = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "KOAK",
                RunwayId = "28R",
                FinalApproachCourse = runway.TrueHeading,
                TransitionName = null,
            },
            AssignedRunway = runway,
            ConnectingFix = null,
        };

        ApproachCommandHandler.ActivatePendingApproach(ac, pending);

        AssertHandedOver(ac);
        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);
        Assert.Null(ac.BravoCap);
        Assert.Null(ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void CrossingAltitudeOverAStandingCap_ThenClbrv_ClimbsToTheCrossingAltitude()
    {
        // The record stands over a controller's "at or below 5,000"; the crossing writes no ceiling of its own.
        AircraftState ac = ClimbingUnderTheShelf();
        (SimulationEngine engine, _) = Engine(ac, solo: true);
        var standing = new BravoCap("SFO", SfoVolumeId(), Cap, 5000, 3500);
        ac.StartBravoCap(standing);
        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);

        CommandResult cfix = engine.SendCommand(Callsign, "CFIX OAK 045");

        Assert.True(cfix.Success, cfix.Message);
        Assert.Null(ac.BravoCap);
        Assert.Equal(standing.PriorCeiling, ac.Targets.AltitudeCeiling);
        Assert.Equal(standing.PriorCeiling, ac.ControllerAltitudeCeiling);
        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        TickSeconds(engine, 90);
        Assert.True(ac.Altitude > Cap + 300, $"cleared into the Class B, climbs past the {Cap} cap toward 4,500; at {ac.Altitude:F0}");
    }

    [Fact]
    public void ApproachClearanceWithACrossingOverALevelOff_ThenClbrv_LeavesNoCapCeiling()
    {
        // Not on an assigned heading, so the clearance builds the procedure (the crossing written after ClearExistingPhases).
        (SimulationEngine engine, AircraftState ac) = LevelOffHoldUnderTheShelf();

        CommandResult capp = engine.SendCommand(Callsign, "CAPP DCT OAK CFIX 045 I28R");

        Assert.True(capp.Success, capp.Message);
        Assert.Null(ac.BravoCap);
        Assert.Null(ac.Targets.AltitudeCeiling); // the level-off hold's prior (none), not its 2,000 cap
        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);
        Assert.Null(ac.BravoCap);
        Assert.Null(ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void AtOrBelowEqualToTheCap_IsKeptAsTheControllerBound()
    {
        AircraftState ac = ClimbingUnderTheShelf();
        (SimulationEngine engine, _) = Engine(ac, solo: true);
        ac.StartBravoCap(new BravoCap("SFO", SfoVolumeId(), Cap, null, 3500));

        Assert.True(engine.SendCommand(Callsign, $"CM B{Cap / 100:D3}").Success);

        Assert.Null(ac.BravoCap);
        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);
        Assert.Equal(Cap, ac.ControllerAltitudeCeiling);
    }

    [Fact]
    public void TakeoffClearanceWithAnAltitude_AfterAnEarlierUnable_DrawsANewOne()
    {
        AircraftState ac = LinedUpAircraft.AtOak28R(Callsign);
        ac.FlightPlan.FlightRules = "VFR";
        (SimulationEngine engine, _) = Engine(ac, solo: true);
        Assert.True(engine.SendCommand(Callsign, "CM 026").Success);
        ac.MarkAirspaceUnableAdvised(); // the unable that command drew earlier
        Assert.False(ac.IsAirspaceUnableDue);

        CommandResult cto = engine.SendCommand(Callsign, "CTO MRH 026");

        Assert.True(cto.Success, cto.Message);
        Assert.True(ac.IsAirspaceUnableDue, "the takeoff clearance's altitude is a new controller command");
        ac.IsOnGround = false; // airborne under the shelf off KOAK 28R
        Steer(ac, UnderSfoShelf, 292.3);
        ac.Altitude = 1500;
        Assert.Equal(1, StepCountingUnables(engine, ac, 3, "unable"));
    }

    [Fact]
    public void WestboundUnderThe4000Shelf_AssignedBelowTheFloor_IsFlownThere_NoUnable()
    {
        LatLon under = FindUnderShelf(4000);
        AircraftState free = InAPhase(under, 270, 2000);
        AircraftState assigned = InAPhase(under, 270, 2000);
        assigned.Targets.AssignedAltitude = 3500;
        assigned.Targets.TargetAltitude = 3500;

        PilotProactive.TickBravoCap(free, SoloScenario(solo: true), AirspaceDatabase.Default);
        PilotProactive.TickBravoCap(assigned, SoloScenario(solo: true), AirspaceDatabase.Default);

        Assert.True(free.BravoCap?.CapFtMsl < 3500, $"premise: the westbound level-off is below 3,500 ({free.BravoCap?.CapFtMsl})");
        Assert.Equal(3500, assigned.BravoCap?.CapFtMsl);
        Assert.Equal(3500, assigned.Targets.AltitudeCeiling);
        Assert.Equal(0, Unables(assigned, "unable"));
    }

    [Fact]
    public void AboveTheClassB_DescendingIntoIt_GetsNoCap()
    {
        AircraftState ac = InAPhase(UnderSfoShelf, 292.3, 10500);
        ac.VerticalSpeed = -1500;
        ac.Targets.TargetAltitude = 1500;
        Assert.NotNull(AirspaceDatabase.Default.FindFirstProjectedEntry(ac, BravoCapScope.LookaheadSeconds, AirspaceClass.Bravo));

        PilotProactive.TickBravoCap(ac, SoloScenario(solo: true), AirspaceDatabase.Default);

        Assert.Null(ac.BravoCap);
        Assert.Null(ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void SessionStopsBeingSolo_TheStandingCapIsReleased()
    {
        (SimulationEngine engine, AircraftState ac) = PatternEntryUnderTheShelf(solo: true, tick: true);
        Assert.NotNull(ac.BravoCap);

        engine.Scenario!.SoloTrainingMode = false;
        engine.TickOneSecond();

        Assert.Null(ac.BravoCap);
        Assert.Null(ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void VfrDepartureFromKsfo_InsideTheSurfaceArea_IsNeverCapped()
    {
        AircraftState fromSfo = DepartureUnderTheShelf("KSFO", "28R");
        AircraftState fromOak = DepartureUnderTheShelf("KOAK", "28R");

        Assert.True(BravoCapScope.IsSurfaceAreaDeparture(fromSfo, AirspaceDatabase.Default));
        Assert.False(BravoCapScope.Applies(fromSfo, AirspaceDatabase.Default));
        PilotProactive.TickBravoCap(fromSfo, SoloScenario(solo: true), AirspaceDatabase.Default);
        PilotProactive.TickBravoCap(fromOak, SoloScenario(solo: true), AirspaceDatabase.Default);

        Assert.Null(fromSfo.BravoCap);
        Assert.Null(fromSfo.Targets.AltitudeCeiling);
        Assert.NotNull(fromOak.BravoCap); // premise: the same aircraft off a runway under the shelf is capped
    }

    [Fact]
    public void IfrInAPhaseUnderTheShelf_IsNeverCapped_CappedOnceItCancelsIfr()
    {
        (SimulationEngine engine, AircraftState ac) = PatternEntryUnderTheShelf(solo: true, tick: false);
        ac.FlightPlan.FlightRules = "IFR";

        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);
        Assert.Null(ac.BravoCap);
        Assert.Null(ac.Targets.AltitudeCeiling);

        ac.FlightPlan.FlightRules = "VFR";
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);

        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void Clbrv_ReleasesTheCap_RestoresThePriorCeilingAndTarget_AndClimbs()
    {
        (SimulationEngine engine, AircraftState ac) = DownwindUnderTheShelf(solo: true);
        (_, int floor) = ReJudgedShelf(engine, ac);
        int through = floor + 500;
        Assert.True(engine.SendCommand(Callsign, Cm(through)).Success);
        Assert.Equal(1, StepCountingUnables(engine, ac, 3, $"unable {through}"));
        BravoCap standing = Assert.IsType<BravoCap>(ac.BravoCap);
        Assert.Equal(through, standing.PriorTargetAltitude);
        ac.Targets.TargetAltitude = null; // physics nulls the target when it captures the capped goal

        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);

        Assert.Null(ac.BravoCap);
        Assert.Equal(standing.PriorCeiling, ac.Targets.AltitudeCeiling);
        Assert.Equal(through, ac.Targets.TargetAltitude);
        ac.Phases = null; // the climb itself, free of the pattern leg's own altitude
        double released = ac.Altitude;
        for (int i = 0; i < 60; i++)
        {
            FlightPhysics.Update(ac, 1.0);
        }

        Assert.True(
            (ac.Altitude > released + 100) && (ac.VerticalSpeed > 100),
            $"cleared into the Class B, climbs from {released:F0} toward {through}; at {ac.Altitude:F0}, {ac.VerticalSpeed:F0} fpm"
        );
    }

    [Fact]
    public void LaterallyClearNowAndAlong60s_ReleasesTheCap_ClearNowButEnteringWithin60s_KeepsIt()
    {
        (SimulationEngine engine, AircraftState ac) = PatternEntryUnderTheShelf(solo: true, tick: true);
        Assert.NotNull(ac.BravoCap);
        LatLon edge = FirstPointOutside(UnderSfoShelf, 90, _ => true);
        LatLon outside = GeoMath.ProjectPoint(edge, new TrueHeading(90), 0.3);

        Steer(ac, outside, 270);
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);
        Assert.NotNull(ac.BravoCap);

        Steer(ac, outside, 90);
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);

        Assert.Null(ac.BravoCap);
        Assert.Null(ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void PhaseChangeOrListReplacement_UnderTheShelf_DoesNotReleaseTheCap()
    {
        (SimulationEngine engine, AircraftState ac) = PatternEntryUnderTheShelf(solo: true, tick: true);
        BravoCap cap = Assert.IsType<BravoCap>(ac.BravoCap);

        Assert.True(engine.SendCommand(Callsign, "ELD 28R").Success);
        Assert.Same(cap, ac.BravoCap);
        engine.TickOneSecond();

        Assert.Same(cap, ac.BravoCap);
        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void ControllerMaintainThroughTheShelf_StaysAtTheCap_OneUnablePerIssuance()
    {
        (SimulationEngine engine, AircraftState ac) = DownwindUnderTheShelf(solo: true);
        (int cap, int floor) = ReJudgedShelf(engine, ac);
        int through = floor + 500;

        Assert.True(engine.SendCommand(Callsign, Cm(through)).Success);
        Assert.Equal(1, StepCountingUnables(engine, ac, 15, $"unable {through}"));
        Assert.IsType<DownwindPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(cap, ac.Targets.AltitudeCeiling);

        Assert.True(engine.SendCommand(Callsign, Cm(through)).Success);
        Assert.Equal(1, StepCountingUnables(engine, ac, 15, $"unable {through}"));
        TickSeconds(engine, 60);
        Assert.True(ac.Altitude <= cap + 50, $"climbed to {ac.Altitude:F0} through the {cap} cap");
    }

    [Fact]
    public void ReleaseAndRestartUnderTheShelf_TouchAndGo_DoesNotRepeatTheUnable()
    {
        (SimulationEngine engine, AircraftState ac) = DownwindUnderTheShelf(solo: true);
        (_, int floor) = ReJudgedShelf(engine, ac);
        Assert.True(engine.SendCommand(Callsign, Cm(floor + 500)).Success);
        Assert.Equal(1, StepCountingUnables(engine, ac, 3, "unable"));

        ac.IsOnGround = true; // the touch-and-go's roll releases the cap
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);
        Assert.Null(ac.BravoCap);
        ac.IsOnGround = false;

        Assert.Equal(0, StepCountingUnables(engine, ac, 5, "unable"));
        Assert.NotNull(ac.BravoCap);
    }

    [Fact]
    public void ControllerMaintainThroughTheShelf_InAnRpoSession_ClimbsThroughWithNoCapAndNoUnable()
    {
        (SimulationEngine engine, AircraftState ac) = DownwindUnderTheShelf(solo: false);
        Assert.Null(ac.BravoCap);
        int floor = BravoCapScope.FindCappingVolume(ac, AirspaceDatabase.Default)!.LowerFtMsl;
        int through = floor + 500;

        Assert.True(engine.SendCommand(Callsign, Cm(through)).Success);
        double highest = ac.Altitude;
        for (int i = 0; i < 400; i++)
        {
            Assert.Equal(0, StepCountingUnables(engine, ac, 1, "unable"));
            Assert.Null(ac.BravoCap);
            engine.TickOneSecond();
            highest = Math.Max(highest, ac.Altitude);
        }

        Assert.True(highest > floor, $"the RPO's climb through the {floor} shelf is flown as given; highest {highest:F0}");
    }

    [Fact]
    public void ControllerAtOrAboveThroughTheShelf_OneUnable_StaysUnder()
    {
        (SimulationEngine engine, AircraftState ac) = DownwindUnderTheShelf(solo: true);
        (int cap, int floor) = ReJudgedShelf(engine, ac);
        int through = floor + 500;

        Assert.True(engine.SendCommand(Callsign, $"CM A{through / 100:D3}").Success);
        Assert.Equal(1, StepCountingUnables(engine, ac, 15, $"unable {through}"));
        Assert.Equal(cap, ac.Targets.AltitudeCeiling);
        TickSeconds(engine, 60);
        Assert.True(ac.Altitude <= cap + 50, $"climbed to {ac.Altitude:F0} through the {cap} cap");
    }

    [Fact]
    public void ControllerAtOrBelowUnderTheCap_CeilingIsTheBound_NoUnable_TheBoundOutlivesTheCap()
    {
        (SimulationEngine engine, AircraftState ac) = DownwindUnderTheShelf(solo: true);

        Assert.True(engine.SendCommand(Callsign, "CM B015").Success);
        Assert.Equal(0, StepCountingUnables(engine, ac, 15, "unable"));

        Assert.Equal(1500, ac.Targets.AltitudeCeiling);
        Assert.NotNull(ac.BravoCap);

        ac.Position = ClearOfClassB;
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);

        Assert.Null(ac.BravoCap);
        Assert.Equal(1500, ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void ControllerAtOrBelowEqualToTheCap_IsTheControllersBound_RestoredOnRelease()
    {
        (SimulationEngine engine, AircraftState ac) = DownwindUnderTheShelf(solo: true);
        (int cap, _) = ReJudgedShelf(engine, ac);

        Assert.True(engine.SendCommand(Callsign, $"CM B{cap / 100:D3}").Success);
        Assert.Equal(0, StepCountingUnables(engine, ac, 3, "unable"));
        Assert.NotNull(ac.BravoCap);
        Assert.Equal(cap, ac.ControllerAltitudeCeiling);

        ac.Position = ClearOfClassB;
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);

        Assert.Null(ac.BravoCap);
        Assert.Equal(cap, ac.Targets.AltitudeCeiling);
    }

    [Theory]
    [InlineData("ERD 28R")]
    [InlineData("CAPP I28R")]
    public void ClearanceReplacingAFreeFlightLevelOff_TheCapTakesItOverInTheSameStep_NoSecondUnable_ClbrvReleases(string command)
    {
        (SimulationEngine engine, AircraftState ac) = LevelOffHoldUnderTheShelf();

        CommandResult result = engine.SendCommand(Callsign, command);

        Assert.True(result.Success, result.Message);
        AssertHandedOver(ac);
        Assert.Equal(0, StepCountingUnables(engine, ac, 5, "unable"));

        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);

        Assert.Null(ac.BravoCap);
        Assert.Null(ac.Targets.AltitudeCeiling);
    }

    [Fact]
    public void HandedOverCap_ReleasedAndStartedAgain_DoesNotRepeatTheHoldsUnable()
    {
        (SimulationEngine engine, AircraftState ac) = LevelOffHoldUnderTheShelf();
        Assert.True(engine.SendCommand(Callsign, "ERD 28R").Success);
        AssertHandedOver(ac);

        ac.IsOnGround = true;
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);
        Assert.Null(ac.BravoCap);
        ac.IsOnGround = false;

        Assert.Equal(0, StepCountingUnables(engine, ac, 3, "unable"));
        Assert.NotNull(ac.BravoCap);
    }

    [Fact]
    public void PhaseChainClearedDuringAFreeFlightLevelOff_TheRecordKeepsTheCap_ClbrvReleasesFully()
    {
        // A command that clears the chain to nothing (CommandDispatcher.ClearPhaseChain) leaves the aircraft in free flight.
        (SimulationEngine engine, AircraftState ac) = LevelOffHoldUnderTheShelf();

        CommandDispatcher.ClearPhaseChain(ac, "test");
        Assert.Null(ac.Phases);
        AssertHandedOver(ac);
        TickSeconds(engine, 5);
        Assert.NotNull(ac.BravoCap);
        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);
        Assert.DoesNotContain(ac.Phases?.Phases ?? [], phase => phase is AirspaceBoundaryHoldPhase);
        ac.Targets.TargetAltitude = null; // physics nulls the target when it captures the capped goal

        Assert.True(engine.SendCommand(Callsign, "CLBRV").Success);
        PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);

        Assert.Null(ac.BravoCap);
        Assert.Null(ac.Targets.AltitudeCeiling);
        Assert.Equal(3500, ac.Targets.TargetAltitude);
    }

    [Fact]
    public void PtacPa_WithTheCapActive_KeepsTheAssignedAltitude()
    {
        (SimulationEngine engine, AircraftState ac) = DownwindUnderTheShelf(solo: true);
        (int cap, _) = ReJudgedShelf(engine, ac);
        Assert.Equal(1500, ac.Targets.AssignedAltitude);
        Assert.Equal(cap, ac.Targets.AltitudeCeiling);
        Assert.Null(ac.ControllerAltitudeCeiling);
        ac.Altitude = 1800;
        ac.VerticalSpeed = 0;

        CommandResult ptac = engine.SendCommand(Callsign, "PTAC PH PA I28R");

        Assert.True(ptac.Success, ptac.Message);
        Assert.Equal(1500, ac.Targets.AssignedAltitude);
    }

    [Fact]
    public void Export_WithTheCapActive_ExportsNoCmBForTheCap()
    {
        (SimulationEngine engine, AircraftState ac) = DownwindUnderTheShelf(solo: true);
        (int cap, _) = ReJudgedShelf(engine, ac);
        Assert.True(engine.SendCommand(Callsign, Cm(cap)).Success);
        TickSeconds(engine, 15);
        Assert.Equal(cap, ac.Targets.AltitudeCeiling);
        Assert.Equal(cap, ac.Targets.AssignedAltitude);
        Assert.Null(ac.ControllerAltitudeCeiling);

        ScenarioExportResult result = ScenarioExporter.Export([ac], ExportContext, new TestAirportGroundData(), _ => null);

        Assert.DoesNotContain(
            Assert.Single(result.Scenario.Aircraft).PresetCommands,
            preset => preset.Command.StartsWith("CM B", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void PatternEntryUnderRmob_IsCapped()
    {
        AircraftState ac = Airborne(UnderSfoShelf, trueHeading: 292.3, altitude: 1500);
        (SimulationEngine engine, _) = Engine(ac, solo: true);
        Assert.True(engine.SendCommand(Callsign, "RMOB").Success);
        Assert.True(ac.IsRemainOutsideBravo("SFO"));

        Assert.True(engine.SendCommand(Callsign, "ERD 28R").Success);
        engine.TickOneSecond();

        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);
        Assert.NotNull(ac.BravoCap);
        Assert.True(ac.IsRemainOutsideBravo("SFO"));
    }

    private static readonly ScenarioExportContext ExportContext = new(
        "ZOA",
        "SFO",
        "2B",
        new DateTime(2026, 9, 23, 14, 5, 0, DateTimeKind.Utc),
        MagneticDeclination.EvaluationDateUtc
    );

    /// <summary>The record took the level-off's cap, its saved (null) ceiling and its 3,500 target, in the same step.</summary>
    private static void AssertHandedOver(AircraftState ac)
    {
        BravoCap cap = Assert.IsType<BravoCap>(ac.BravoCap);
        Assert.Equal(Cap, cap.CapFtMsl);
        Assert.Null(cap.PriorCeiling);
        Assert.Equal(3500, cap.PriorTargetAltitude);
        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);
    }

    /// <summary>
    /// A VFR aircraft climbing to an assigned 3,500 under the shelf in free flight, two engine seconds in: the free-flight
    /// level-off hold caps it at 2,000 and has said unable once; the queue is emptied.
    /// </summary>
    private static (SimulationEngine Engine, AircraftState Aircraft) LevelOffHoldUnderTheShelf()
    {
        AircraftState ac = ClimbingUnderTheShelf();
        (SimulationEngine engine, _) = Engine(ac, solo: true);
        TickSeconds(engine, 2);
        AirspaceBoundaryHoldPhase hold = Assert.IsType<AirspaceBoundaryHoldPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(AirspaceHoldMode.LevelOff, hold.Mode);
        Assert.Equal(Cap, ac.Targets.AltitudeCeiling);
        Assert.Null(ac.BravoCap);
        Assert.Equal(ac.ControllerAltitudeCommandCount, ac.AirspaceUnableAdvisedCommand);
        ac.PendingPilotTransmissions.Clear();
        return (engine, ac);
    }

    private static AircraftState ClimbingUnderTheShelf()
    {
        AircraftState ac = Airborne(UnderSfoShelf, trueHeading: 292.3, altitude: 1485);
        ac.VerticalSpeed = 926;
        ac.Targets.TargetAltitude = 3500;
        ac.Targets.AssignedAltitude = 3500;
        return ac;
    }

    /// <summary>
    /// "CM 015" under the downwind's own shelf: the cap is judged afresh there (no unable, 1,500 is below it); returns that
    /// cap and the floor it sits under.
    /// </summary>
    private static (int Cap, int Floor) ReJudgedShelf(SimulationEngine engine, AircraftState ac)
    {
        Assert.True(engine.SendCommand(Callsign, "CM 015").Success);
        Assert.Equal(0, StepCountingUnables(engine, ac, 15, "unable"));
        BravoCap cap = Assert.IsType<BravoCap>(ac.BravoCap);
        return (cap.CapFtMsl, AirspaceDatabase.Default.FindById(cap.VolumeId)!.LowerFtMsl);
    }

    private static string Cm(int altitude) => $"CM {altitude / 100:D3}";

    /// <summary>
    /// Runs the once-per-second cap step <paramref name="seconds"/> times, counting (and removing) the queued transmissions
    /// that hold <paramref name="text"/>. The step alone, not the engine: an engine second drains the queue to the frequency.
    /// </summary>
    private static int StepCountingUnables(SimulationEngine engine, AircraftState ac, int seconds, string text)
    {
        int count = 0;
        for (int i = 0; i < seconds; i++)
        {
            PilotProactive.TickBravoCap(ac, engine.Scenario!, AirspaceDatabase.Default);
            count += ac.PendingPilotTransmissions.RemoveAll(t => t.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        return count;
    }

    private static int Unables(AircraftState ac, string text) =>
        ac.PendingPilotTransmissions.Count(t => t.Text.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>A VFR aircraft under the shelf entering the KOAK 28R pattern, optionally one engine second in.</summary>
    private static (SimulationEngine Engine, AircraftState Aircraft) PatternEntryUnderTheShelf(bool solo, bool tick)
    {
        AircraftState ac = Airborne(UnderSfoShelf, trueHeading: 292.3, altitude: 1500);
        (SimulationEngine engine, _) = Engine(ac, solo);
        CommandResult entry = engine.SendCommand(Callsign, "ERD 28R");
        Assert.True(entry.Success, entry.Message);
        Assert.Null(ac.ClearedBravoIdent); // a satellite's pattern entry is no Class B clearance
        if (tick)
        {
            engine.TickOneSecond();
        }

        return (engine, ac);
    }

    /// <summary>
    /// The same aircraft flown on into the KOAK 28R right downwind, still under the shelf: a leg that takes an altitude
    /// assignment without leaving the pattern (a pattern entry is cleared by one).
    /// </summary>
    private static (SimulationEngine Engine, AircraftState Aircraft) DownwindUnderTheShelf(bool solo)
    {
        (SimulationEngine engine, AircraftState ac) = PatternEntryUnderTheShelf(solo, tick: true);
        for (int i = 0; (i < 600) && (ac.Phases?.CurrentPhase is not DownwindPhase); i++)
        {
            engine.TickOneSecond();
        }

        Assert.IsType<DownwindPhase>(ac.Phases?.CurrentPhase);
        Assert.True(AirspaceDatabase.Default.IsUnderClassBShelf(ac.Position, ac.Altitude), "premise: the downwind runs under the shelf");
        Assert.Equal(solo ? Cap : null, ac.Targets.AltitudeCeiling);
        ac.PendingPilotTransmissions.Clear();
        return (engine, ac);
    }

    /// <summary>A VFR aircraft flying an initial climb with no runway (so no surface-area exemption): a phase in scope.</summary>
    private static AircraftState InAPhase(LatLon position, double trueHeading, double altitude)
    {
        AircraftState ac = Airborne(position, trueHeading, altitude);
        ac.Phases = new PhaseList();
        ac.Phases.Add(
            new InitialClimbPhase
            {
                Departure = new DefaultDeparture(),
                IsVfr = true,
                CruiseAltitude = 3500,
            }
        );
        ac.Phases.Phases[0].Status = PhaseStatus.Active;
        return ac;
    }

    private static void Steer(AircraftState ac, LatLon position, double trueHeading)
    {
        ac.Position = position;
        ac.TrueHeading = new TrueHeading(trueHeading);
        ac.TrueTrack = new TrueHeading(trueHeading);
    }

    private static IReadOnlyList<AirspaceVolume> Sfo => AirspaceDatabase.Default.ClassBArea("SFO");

    private static string SfoVolumeId() => Sfo[0].Id;

    /// <summary>The lowest SFO floor laterally over <paramref name="point"/>, or null outside the footprint.</summary>
    private static int? LowestSfoFloor(LatLon point) =>
        Sfo.Where(v => v.ContainsLateral(point)).Select(v => (int?)v.LowerFtMsl).DefaultIfEmpty(null).Min();

    /// <summary>Walks from <paramref name="from"/> along <paramref name="bearing"/> to the first point no matching SFO volume covers.</summary>
    private static LatLon FirstPointOutside(LatLon from, double bearing, Func<AirspaceVolume, bool> matches)
    {
        for (double nm = 0; nm < 60; nm += 0.1)
        {
            LatLon point = GeoMath.ProjectPoint(from, new TrueHeading(bearing), nm);
            if (!Sfo.Any(v => matches(v) && v.ContainsLateral(point)))
            {
                return point;
            }
        }

        throw new InvalidOperationException("no edge found");
    }

    private static IEnumerable<LatLon> SfoGrid()
    {
        for (double lat = 37.30; lat <= 38.00; lat += 0.02)
        {
            for (double lon = -122.80; lon <= -121.90; lon += 0.02)
            {
                yield return new LatLon(lat, lon);
            }
        }
    }

    /// <summary>Whether the lowest SFO floor stays <paramref name="floor"/> for 2 nm east and west of <paramref name="point"/>.</summary>
    private static bool StaysUnderFloor(LatLon point, int floor)
    {
        for (double nm = 0; nm <= 2.0; nm += 0.25)
        {
            if (
                (LowestSfoFloor(GeoMath.ProjectPoint(point, new TrueHeading(90), nm)) != floor)
                || (LowestSfoFloor(GeoMath.ProjectPoint(point, new TrueHeading(270), nm)) != floor)
            )
            {
                return false;
            }
        }

        return true;
    }

    private static LatLon FindUnderShelf(int floor) => SfoGrid().First(p => LowestSfoFloor(p) == floor);

    /// <summary>
    /// A point under one SFO shelf only whose eastbound track reaches, 1 nm on, a shelf at least 1,000 ft lower (but not the
    /// surface area): the upper and lower volumes, from the real SFO Class B.
    /// </summary>
    private static (LatLon From, double Heading, AirspaceVolume Upper, AirspaceVolume Lower) FindStepDown()
    {
        foreach (LatLon point in SfoGrid())
        {
            if ((LowestSfoFloor(point) is not { } upperFloor) || (upperFloor < 3000))
            {
                continue;
            }

            for (double heading = 40; heading <= 160; heading += 20)
            {
                LatLon ahead = GeoMath.ProjectPoint(point, new TrueHeading(heading), 1.0);
                if ((LowestSfoFloor(ahead) is { } lowerFloor) && (lowerFloor > 0) && (lowerFloor <= upperFloor - 1000))
                {
                    AirspaceVolume upper = Sfo.First(v => (v.LowerFtMsl == upperFloor) && v.ContainsLateral(point));
                    AirspaceVolume lower = Sfo.First(v => (v.LowerFtMsl == lowerFloor) && v.ContainsLateral(ahead));
                    return (point, heading, upper, lower);
                }
            }
        }

        throw new InvalidOperationException("no step-down found in the SFO Class B");
    }

    private static LatLon? LookupAirport(string id) =>
        id switch
        {
            "OAK" or "KOAK" => new LatLon(37.7213, -122.2208),
            "SFO" or "KSFO" => Ksfo,
            _ => null,
        };

    private static AircraftState DepartureAtRunwayEnd(string airport, string runwayId)
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway(airport, runwayId)!;
        var end = new LatLon(runway.EndLatitude, runway.EndLongitude);
        AircraftState ac = Airborne(end, runway.TrueHeading.Degrees, runway.AirportElevationFt + 400);
        ac.FlightPlan.Departure = airport;
        ac.VerticalSpeed = 700;
        ac.Phases = DeparturePhases(runway);
        return ac;
    }

    private static AircraftState DepartureUnderTheShelf(string airport, string runwayId)
    {
        RunwayInfo runway = NavigationDatabase.Instance.GetRunway(airport, runwayId)!;
        AircraftState ac = Airborne(UnderSfoShelf, trueHeading: 292.3, altitude: 1500);
        ac.FlightPlan.Departure = airport;
        ac.Phases = DeparturePhases(runway);
        ac.Phases.Phases[0].Status = PhaseStatus.Active;
        return ac;
    }

    private static PhaseList DeparturePhases(RunwayInfo runway)
    {
        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(
            new InitialClimbPhase
            {
                Departure = new DefaultDeparture(),
                IsVfr = true,
                CruiseAltitude = 3500,
            }
        );
        return phases;
    }

    private static void TickSeconds(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    private static (SimulationEngine Engine, SimScenarioState Scenario) Engine(AircraftState ac, bool solo)
    {
        SimScenarioState scenario = SoloScenario(solo);
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped()) { Scenario = scenario };
        engine.World.AddAircraft(ac);
        return (engine, scenario);
    }

    private static AircraftState Airborne(LatLon position, double trueHeading, double altitude)
    {
        var ac = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C182",
            Position = position,
            TrueHeading = new TrueHeading(trueHeading),
            TrueTrack = new TrueHeading(trueHeading),
            Altitude = altitude,
            IndicatedAirspeed = 100,
            FlightPlan = new AircraftFlightPlan
            {
                FlightRules = "VFR",
                HasFlightPlan = true,
                Departure = "KOAK",
                Destination = "KOAK",
            },
        };
        ac.Targets.TargetTrueHeading = new TrueHeading(trueHeading);
        ac.HasMadeInitialContact = true;
        ac.HasControllerAcknowledgedInitialContact = true;
        return ac;
    }

    private static SimScenarioState SoloScenario(bool solo) =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            SoloTrainingMode = solo,
            PrimaryAirportId = "OAK",
            StudentPositionType = "TWR",
        };

    private static PhaseContext Context(AircraftState ac, bool solo, double fieldElevation) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
            SoloTrainingMode = solo,
            StudentPositionType = "TWR",
            FieldElevation = fieldElevation,
        };
}
