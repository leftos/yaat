using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// P4.3: End-to-end pattern circuit tests.
/// Builds phase lists and ticks through them to verify complete pattern circuits.
/// </summary>
[Collection("NavDbMutator")]
public class PatternCircuitE2ETests : IDisposable
{
    private readonly IDisposable _navDbScope;

    public PatternCircuitE2ETests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(TestNavDbFactory.WithRunways(DefaultRunway()));
    }

    public void Dispose() => _navDbScope.Dispose();

    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

    private static AircraftState MakeAircraft(RunwayInfo rwy, double altitude, double heading, double ias = 200)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude),
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "TEST" },
            Phases = new PhaseList { AssignedRunway = rwy },
        };
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, double dt = 1.0)
    {
        RunwayInfo rwy = ac.Phases!.AssignedRunway!;
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = rwy.ElevationFt,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
        };
    }

    /// <summary>
    /// Ticks the aircraft through phases, moving it toward waypoints each tick.
    /// Returns the list of phase type names completed.
    /// </summary>
    private static List<string> RunCircuit(AircraftState ac, int maxTicks = 2000)
    {
        var completedPhases = new List<string>();
        string? lastPhaseName = null;

        for (int i = 0; i < maxTicks; i++)
        {
            Phase? current = ac.Phases?.CurrentPhase;
            if (current is null || ac.Phases!.IsComplete)
            {
                break;
            }

            string phaseName = current.GetType().Name;
            if (phaseName != lastPhaseName)
            {
                if (lastPhaseName is not null)
                {
                    completedPhases.Add(lastPhaseName);
                }
                lastPhaseName = phaseName;
            }

            PhaseContext ctx = Ctx(ac);

            // Move aircraft toward its targets each tick
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);
        }

        // Add the final phase if we ended cleanly
        if (lastPhaseName is not null && ac.Phases?.IsComplete == true)
        {
            completedPhases.Add(lastPhaseName);
        }

        return completedPhases;
    }

    // -------------------------------------------------------------------------
    // P4.3: Full pattern circuit
    // -------------------------------------------------------------------------

    [Fact]
    public void FullCircuit_FromUpwind_CompletesAllPhases()
    {
        RunwayInfo rwy = DefaultRunway();
        AircraftState ac = MakeAircraft(rwy, altitude: rwy.ElevationFt + 200, heading: rwy.TrueHeading.Degrees, ias: 180);

        // Build a non-touch-and-go circuit (landing)
        List<Phase> phases = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            PatternEntryLeg.Upwind,
            false,
            null,
            null,
            null,
            null,
            authoredRunway: null
        );
        foreach (Phase p in phases)
        {
            ac.Phases!.Add(p);
        }
        ac.Phases!.Start(Ctx(ac));

        // Set landing clearance so FinalApproachPhase doesn't auto-go-around
        ac.Phases.LandingClearance = ClearanceType.ClearedToLand;

        List<string> completed = RunCircuit(ac);

        // Should pass through: Upwind, Crosswind, Downwind, Base, FinalApproach, Landing
        Assert.Contains("UpwindPhase", completed);
        Assert.Contains("CrosswindPhase", completed);
        Assert.Contains("DownwindPhase", completed);
        Assert.Contains("BasePhase", completed);
        Assert.Contains("FinalApproachPhase", completed);
    }

    [Fact]
    public void FullCircuit_FromDownwind_SkipsUpwindCrosswind()
    {
        RunwayInfo rwy = DefaultRunway();
        PatternWaypoints wp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            null,
            null,
            null,
            authoredRunway: null
        );
        AircraftState ac = MakeAircraft(rwy, altitude: wp.PatternAltitude, heading: wp.DownwindHeading.Degrees);
        ac.Position = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);

        List<Phase> phases = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            false,
            null,
            null,
            null,
            null,
            authoredRunway: null
        );
        foreach (Phase p in phases)
        {
            ac.Phases!.Add(p);
        }
        ac.Phases!.Start(Ctx(ac));
        ac.Phases.LandingClearance = ClearanceType.ClearedToLand;

        List<string> completed = RunCircuit(ac);

        // Should NOT have Upwind or Crosswind
        Assert.DoesNotContain("UpwindPhase", completed);
        Assert.DoesNotContain("CrosswindPhase", completed);
        // Should have Downwind, Base, FinalApproach
        Assert.Contains("DownwindPhase", completed);
        Assert.Contains("BasePhase", completed);
        Assert.Contains("FinalApproachPhase", completed);
    }

    // -------------------------------------------------------------------------
    // P4.3: Touch-and-go -> second circuit
    // -------------------------------------------------------------------------

    [Fact]
    public void TouchAndGo_AutoCyclesIntoNextCircuit()
    {
        RunwayInfo rwy = DefaultRunway();
        PatternWaypoints wp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            null,
            null,
            null,
            authoredRunway: null
        );
        AircraftState ac = MakeAircraft(rwy, altitude: wp.PatternAltitude, heading: wp.DownwindHeading.Degrees);
        ac.Position = new LatLon(wp.DownwindAbeamLat, wp.DownwindAbeamLon);

        // Build a touch-and-go circuit from downwind
        List<Phase> phases = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            true,
            null,
            null,
            null,
            null,
            authoredRunway: null
        );
        foreach (Phase p in phases)
        {
            ac.Phases!.Add(p);
        }
        ac.Phases!.TrafficDirection = PatternDirection.Left;
        ac.Phases.Start(Ctx(ac));
        ac.Phases.LandingClearance = ClearanceType.ClearedTouchAndGo;

        // Run until the touch-and-go phase completes and auto-cycle kicks in
        string? lastPhaseName = null;
        bool sawSecondUpwind = false;
        bool sawTouchAndGo = false;

        for (int i = 0; i < 3000; i++)
        {
            Phase? current = ac.Phases?.CurrentPhase;
            if (current is null)
            {
                break;
            }

            string phaseName = current.GetType().Name;
            if (phaseName != lastPhaseName)
            {
                if (sawTouchAndGo && phaseName == "UpwindPhase")
                {
                    sawSecondUpwind = true;
                    break;
                }
                if (phaseName == "TouchAndGoPhase")
                {
                    sawTouchAndGo = true;
                }
                lastPhaseName = phaseName;
            }

            PhaseContext ctx = Ctx(ac);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);
        }

        Assert.True(sawTouchAndGo, "Should have reached TouchAndGoPhase");
        Assert.True(sawSecondUpwind, "After touch-and-go, should auto-cycle into second circuit (UpwindPhase)");
    }

    // -------------------------------------------------------------------------
    // P4.3: Go-around from final
    // -------------------------------------------------------------------------

    [Fact]
    public void GoAround_FromFinal_ClearsPhases()
    {
        RunwayInfo rwy = DefaultRunway();
        AircraftState ac = MakeAircraft(rwy, altitude: rwy.ElevationFt + 800, heading: rwy.TrueHeading.Degrees, ias: 150);

        // Set up on final (close to runway)
        LatLon approachPos = GeoMath.ProjectPoint(new LatLon(rwy.ThresholdLatitude, rwy.ThresholdLongitude), rwy.TrueHeading.ToReciprocal(), 3.0);
        ac.Position = approachPos;

        // Build circuit from final entry
        List<Phase> phases = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            PatternEntryLeg.Final,
            false,
            null,
            null,
            null,
            null,
            authoredRunway: null
        );
        foreach (Phase p in phases)
        {
            ac.Phases!.Add(p);
        }
        ac.Phases!.Start(Ctx(ac));
        // No landing clearance → should auto-go-around at 0.5nm

        // Run a few ticks to get FinalApproachPhase started
        for (int i = 0; i < 50; i++)
        {
            PhaseContext ctx = Ctx(ac);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);
        }

        // Issue go-around command via DispatchCompound (phase interaction path)
        var compound = new CompoundCommand([new ParsedBlock(null, [new GoAroundCommand(null, null, null)])]);
        CommandResult result = CommandDispatcher.DispatchCompound(compound, ac, TestDispatch.Context(Random.Shared));

        // Go-around should succeed (clears phase, sets up GoAroundPhase)
        Assert.True(result.Success, $"Go-around should succeed, got: {result.Message}");
        Assert.NotNull(ac.Phases?.CurrentPhase);
        Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
    }

    [Fact]
    public void GoAround_PatternMode_TargetAltitudeIs300BelowPatternAltitude()
    {
        // AIM 4-3-2: VFR pattern traffic may turn crosswind once within 300ft of
        // pattern altitude. Auto-triggered go-arounds should hand off to UpwindPhase
        // 300ft below pattern altitude so the turn matches a normal departure.
        RunwayInfo rwy = DefaultRunway();
        PatternWaypoints wp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            null,
            null,
            null,
            authoredRunway: null
        );
        AircraftState ac = MakeAircraft(rwy, altitude: rwy.ElevationFt + 400, heading: rwy.TrueHeading.Degrees, ias: 150);
        ac.Phases!.TrafficDirection = PatternDirection.Left;
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Start(Ctx(ac));

        GoAroundHelper.Trigger(Ctx(ac), "test");

        GoAroundPhase ga = Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        Assert.True(ga.ReenterPattern);
        Assert.Equal((int)(wp.PatternAltitude - 300), ga.TargetAltitude);
    }

    // -------------------------------------------------------------------------
    // GoAround intent preservation
    // -------------------------------------------------------------------------

    [Fact]
    public void GoAroundHelper_PendingLandingPhase_CapturesFullStopIntent()
    {
        RunwayInfo rwy = DefaultRunway();
        AircraftState ac = MakeAircraft(rwy, altitude: rwy.ElevationFt + 400, heading: rwy.TrueHeading.Degrees, ias: 150);
        ac.Phases!.TrafficDirection = PatternDirection.Left;

        // Aircraft on final with a pending full-stop landing.
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(new LandingPhase());
        ac.Phases.Start(Ctx(ac));

        GoAroundHelper.Trigger(Ctx(ac), "test");

        GoAroundPhase ga = Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        Assert.True(ga.NextLandingFullStop, "GoAroundHelper should capture full-stop intent from pending LandingPhase");
    }

    [Fact]
    public void GoAroundHelper_PendingTouchAndGoPhase_CapturesTouchAndGoIntent()
    {
        RunwayInfo rwy = DefaultRunway();
        AircraftState ac = MakeAircraft(rwy, altitude: rwy.ElevationFt + 400, heading: rwy.TrueHeading.Degrees, ias: 150);
        ac.Phases!.TrafficDirection = PatternDirection.Left;

        // Pattern aircraft on final with a pending touch-and-go.
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(new TouchAndGoPhase());
        ac.Phases.Start(Ctx(ac));

        GoAroundHelper.Trigger(Ctx(ac), "test");

        GoAroundPhase ga = Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
        Assert.False(ga.NextLandingFullStop, "GoAroundHelper should capture touch-and-go intent from pending TouchAndGoPhase");
    }

    [Fact]
    public void AutoCycle_AfterGoAroundFromLandingIntent_NextCircuitEndsWithLandingPhase()
    {
        RunwayInfo rwy = DefaultRunway();
        AircraftState ac = MakeAircraft(rwy, altitude: rwy.ElevationFt + 400, heading: rwy.TrueHeading.Degrees, ias: 150);
        ac.Phases!.TrafficDirection = PatternDirection.Left;

        // Aircraft on final with full-stop intent.
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(new LandingPhase());
        ac.Phases.Start(Ctx(ac));

        GoAroundHelper.Trigger(Ctx(ac), "test");
        GoAroundPhase ga = Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);

        // Bump altitude past the GA target so OnTick completes on the next tick.
        Assert.NotNull(ga.TargetAltitude);
        ac.Altitude = ga.TargetAltitude!.Value + 100;

        PhaseRunner.Tick(ac, Ctx(ac));

        // Auto-cycle should have appended a next circuit ending in LandingPhase
        // (preserved full-stop intent), not TouchAndGoPhase.
        Assert.Contains(ac.Phases.Phases, p => p is UpwindPhase);
        Phase lastPending = ac.Phases.Phases.Last();
        Assert.IsType<LandingPhase>(lastPending);
    }

    [Fact]
    public void AutoCycle_AfterGoAroundFromTouchAndGoIntent_NextCircuitEndsWithTouchAndGoPhase()
    {
        RunwayInfo rwy = DefaultRunway();
        AircraftState ac = MakeAircraft(rwy, altitude: rwy.ElevationFt + 400, heading: rwy.TrueHeading.Degrees, ias: 150);
        ac.Phases!.TrafficDirection = PatternDirection.Left;

        // Pattern aircraft on final with touch-and-go intent.
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(new TouchAndGoPhase());
        ac.Phases.Start(Ctx(ac));

        GoAroundHelper.Trigger(Ctx(ac), "test");
        GoAroundPhase ga = Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);

        Assert.NotNull(ga.TargetAltitude);
        ac.Altitude = ga.TargetAltitude!.Value + 100;

        PhaseRunner.Tick(ac, Ctx(ac));

        // Auto-cycle should have appended a next circuit ending in TouchAndGoPhase.
        Assert.Contains(ac.Phases.Phases, p => p is UpwindPhase);
        Phase lastPending = ac.Phases.Phases.Last();
        Assert.IsType<TouchAndGoPhase>(lastPending);
    }

    // -------------------------------------------------------------------------
    // PatternBuilder unit tests
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildCircuit_Upwind_HasAllPhases()
    {
        RunwayInfo rwy = DefaultRunway();
        List<Phase> phases = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            PatternEntryLeg.Upwind,
            false,
            null,
            null,
            null,
            null,
            authoredRunway: null
        );

        Assert.Equal(6, phases.Count);
        Assert.IsType<UpwindPhase>(phases[0]);
        Assert.IsType<CrosswindPhase>(phases[1]);
        Assert.IsType<DownwindPhase>(phases[2]);
        Assert.IsType<BasePhase>(phases[3]);
        Assert.IsType<FinalApproachPhase>(phases[4]);
        Assert.IsType<LandingPhase>(phases[5]);
    }

    [Fact]
    public void BuildCircuit_Downwind_SkipsUpwindCrosswind()
    {
        RunwayInfo rwy = DefaultRunway();
        List<Phase> phases = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            PatternEntryLeg.Downwind,
            false,
            null,
            null,
            null,
            null,
            authoredRunway: null
        );

        Assert.Equal(4, phases.Count);
        Assert.IsType<DownwindPhase>(phases[0]);
        Assert.IsType<BasePhase>(phases[1]);
        Assert.IsType<FinalApproachPhase>(phases[2]);
        Assert.IsType<LandingPhase>(phases[3]);
    }

    [Fact]
    public void BuildCircuit_Base_SkipsDownwind()
    {
        RunwayInfo rwy = DefaultRunway();
        List<Phase> phases = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            PatternEntryLeg.Base,
            false,
            null,
            null,
            null,
            null,
            authoredRunway: null
        );

        Assert.Equal(3, phases.Count);
        Assert.IsType<BasePhase>(phases[0]);
        Assert.IsType<FinalApproachPhase>(phases[1]);
        Assert.IsType<LandingPhase>(phases[2]);
    }

    [Fact]
    public void BuildCircuit_Final_OnlyFinalAndLanding()
    {
        RunwayInfo rwy = DefaultRunway();
        List<Phase> phases = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            PatternEntryLeg.Final,
            false,
            null,
            null,
            null,
            null,
            authoredRunway: null
        );

        Assert.Equal(2, phases.Count);
        Assert.IsType<FinalApproachPhase>(phases[0]);
        Assert.IsType<LandingPhase>(phases[1]);
    }

    [Fact]
    public void BuildCircuit_TouchAndGo_ReplacesFinalPhase()
    {
        RunwayInfo rwy = DefaultRunway();
        List<Phase> phases = PatternBuilder.BuildCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            PatternEntryLeg.Final,
            true,
            null,
            null,
            null,
            null,
            authoredRunway: null
        );

        Assert.Equal(2, phases.Count);
        Assert.IsType<FinalApproachPhase>(phases[0]);
        Assert.IsType<TouchAndGoPhase>(phases[1]);
    }

    [Fact]
    public void BuildNextCircuit_TouchAndGo_IsFullCircuitWithTouchAndGo()
    {
        RunwayInfo rwy = DefaultRunway();
        List<Phase> phases = PatternBuilder.BuildNextCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Right,
            null,
            null,
            null,
            authoredRunway: null,
            touchAndGo: true
        );

        Assert.Equal(6, phases.Count);
        Assert.IsType<UpwindPhase>(phases[0]);
        Assert.IsType<TouchAndGoPhase>(phases[5]);
    }

    [Fact]
    public void BuildNextCircuit_FullStop_EndsWithLandingPhase()
    {
        RunwayInfo rwy = DefaultRunway();
        List<Phase> phases = PatternBuilder.BuildNextCircuit(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Right,
            null,
            null,
            null,
            authoredRunway: null,
            touchAndGo: false
        );

        Assert.Equal(6, phases.Count);
        Assert.IsType<UpwindPhase>(phases[0]);
        Assert.IsType<LandingPhase>(phases[5]);
    }

    [Fact]
    public void UpdateWaypoints_UpdatesAllPatternPhases()
    {
        RunwayInfo rwy = DefaultRunway();
        var phaseList = new PhaseList { AssignedRunway = rwy };
        PatternWaypoints oldWp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Left,
            null,
            null,
            null,
            authoredRunway: null
        );
        PatternWaypoints newWp = PatternGeometry.Compute(
            rwy,
            AircraftCategory.Jet,
            "",
            0,
            PatternDirection.Right,
            null,
            null,
            null,
            authoredRunway: null
        );

        var downwind = new DownwindPhase { Waypoints = oldWp };
        var basep = new BasePhase { Waypoints = oldWp };
        phaseList.Add(downwind);
        phaseList.Add(basep);

        bool updated = PatternBuilder.UpdateWaypoints(phaseList, newWp);

        Assert.True(updated);
        Assert.Equal(newWp.DownwindHeading, downwind.Waypoints.DownwindHeading);
        Assert.Equal(newWp.BaseHeading, basep.Waypoints.BaseHeading);
    }
}
