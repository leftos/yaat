using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// P4.3: End-to-end pattern circuit tests.
/// Builds phase lists and ticks through them to verify complete pattern circuits.
/// </summary>
public class PatternCircuitE2ETests
{
    private static RunwayInfo DefaultRunway() => TestRunwayFactory.Make(designator: "28", heading: 280, elevationFt: 100);

    private static AircraftState MakeAircraft(RunwayInfo rwy, double altitude, double heading, double ias = 200)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = rwy.ThresholdLatitude,
            Longitude = rwy.ThresholdLongitude,
            TrueHeading = new TrueHeading(heading),
            TrueTrack = new TrueHeading(heading),
            Altitude = altitude,
            IndicatedAirspeed = ias,
            IsOnGround = false,
            Departure = "TEST",
        };
        ac.Phases = new PhaseList { AssignedRunway = rwy };
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, double dt = 1.0)
    {
        var rwy = ac.Phases!.AssignedRunway!;
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
            var current = ac.Phases?.CurrentPhase;
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

            var ctx = Ctx(ac);

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
        var rwy = DefaultRunway();
        var ac = MakeAircraft(rwy, altitude: rwy.ElevationFt + 200, heading: rwy.TrueHeading.Degrees, ias: 180);

        // Build a non-touch-and-go circuit (landing)
        var phases = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Jet, PatternDirection.Left, PatternEntryLeg.Upwind, false);
        foreach (var p in phases)
        {
            ac.Phases!.Add(p);
        }
        ac.Phases!.Start(Ctx(ac));

        // Set landing clearance so FinalApproachPhase doesn't auto-go-around
        ac.Phases.LandingClearance = ClearanceType.ClearedToLand;

        var completed = RunCircuit(ac);

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
        var rwy = DefaultRunway();
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left);
        var ac = MakeAircraft(rwy, altitude: wp.PatternAltitude, heading: wp.DownwindHeading.Degrees);
        ac.Latitude = wp.DownwindAbeamLat;
        ac.Longitude = wp.DownwindAbeamLon;

        var phases = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Jet, PatternDirection.Left, PatternEntryLeg.Downwind, false);
        foreach (var p in phases)
        {
            ac.Phases!.Add(p);
        }
        ac.Phases!.Start(Ctx(ac));
        ac.Phases.LandingClearance = ClearanceType.ClearedToLand;

        var completed = RunCircuit(ac);

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
        var rwy = DefaultRunway();
        var wp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left);
        var ac = MakeAircraft(rwy, altitude: wp.PatternAltitude, heading: wp.DownwindHeading.Degrees);
        ac.Latitude = wp.DownwindAbeamLat;
        ac.Longitude = wp.DownwindAbeamLon;

        // Build a touch-and-go circuit from downwind
        var phases = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Jet, PatternDirection.Left, PatternEntryLeg.Downwind, true);
        foreach (var p in phases)
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
            var current = ac.Phases?.CurrentPhase;
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

            var ctx = Ctx(ac);
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
        var rwy = DefaultRunway();
        var ac = MakeAircraft(rwy, altitude: rwy.ElevationFt + 800, heading: rwy.TrueHeading.Degrees, ias: 150);

        // Set up on final (close to runway)
        var (approachLat, approachLon) = GeoMath.ProjectPoint(rwy.ThresholdLatitude, rwy.ThresholdLongitude, rwy.TrueHeading.ToReciprocal(), 3.0);
        ac.Latitude = approachLat;
        ac.Longitude = approachLon;

        // Build circuit from final entry
        var phases = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Jet, PatternDirection.Left, PatternEntryLeg.Final, false);
        foreach (var p in phases)
        {
            ac.Phases!.Add(p);
        }
        ac.Phases!.Start(Ctx(ac));
        // No landing clearance → should auto-go-around at 0.5nm

        // Run a few ticks to get FinalApproachPhase started
        for (int i = 0; i < 50; i++)
        {
            var ctx = Ctx(ac);
            FlightPhysics.Update(ac, ctx.DeltaSeconds);
            PhaseRunner.Tick(ac, ctx);
        }

        // Issue go-around command via DispatchCompound (phase interaction path)
        var compound = new CompoundCommand([new ParsedBlock(null, [new GoAroundCommand(null, null, null)])]);
        var result = CommandDispatcher.DispatchCompound(compound, ac, null, Random.Shared, true);

        // Go-around should succeed (clears phase, sets up GoAroundPhase)
        Assert.True(result.Success, $"Go-around should succeed, got: {result.Message}");
        Assert.NotNull(ac.Phases?.CurrentPhase);
        Assert.IsType<GoAroundPhase>(ac.Phases!.CurrentPhase);
    }

    // -------------------------------------------------------------------------
    // PatternBuilder unit tests
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildCircuit_Upwind_HasAllPhases()
    {
        var rwy = DefaultRunway();
        var phases = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Jet, PatternDirection.Left, PatternEntryLeg.Upwind, false);

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
        var rwy = DefaultRunway();
        var phases = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Jet, PatternDirection.Left, PatternEntryLeg.Downwind, false);

        Assert.Equal(4, phases.Count);
        Assert.IsType<DownwindPhase>(phases[0]);
        Assert.IsType<BasePhase>(phases[1]);
        Assert.IsType<FinalApproachPhase>(phases[2]);
        Assert.IsType<LandingPhase>(phases[3]);
    }

    [Fact]
    public void BuildCircuit_Base_SkipsDownwind()
    {
        var rwy = DefaultRunway();
        var phases = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Jet, PatternDirection.Left, PatternEntryLeg.Base, false);

        Assert.Equal(3, phases.Count);
        Assert.IsType<BasePhase>(phases[0]);
        Assert.IsType<FinalApproachPhase>(phases[1]);
        Assert.IsType<LandingPhase>(phases[2]);
    }

    [Fact]
    public void BuildCircuit_Final_OnlyFinalAndLanding()
    {
        var rwy = DefaultRunway();
        var phases = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Jet, PatternDirection.Left, PatternEntryLeg.Final, false);

        Assert.Equal(2, phases.Count);
        Assert.IsType<FinalApproachPhase>(phases[0]);
        Assert.IsType<LandingPhase>(phases[1]);
    }

    [Fact]
    public void BuildCircuit_TouchAndGo_ReplacesFinalPhase()
    {
        var rwy = DefaultRunway();
        var phases = PatternBuilder.BuildCircuit(rwy, AircraftCategory.Jet, PatternDirection.Left, PatternEntryLeg.Final, true);

        Assert.Equal(2, phases.Count);
        Assert.IsType<FinalApproachPhase>(phases[0]);
        Assert.IsType<TouchAndGoPhase>(phases[1]);
    }

    [Fact]
    public void BuildNextCircuit_IsFullCircuitWithTouchAndGo()
    {
        var rwy = DefaultRunway();
        var phases = PatternBuilder.BuildNextCircuit(rwy, AircraftCategory.Jet, PatternDirection.Right);

        Assert.Equal(6, phases.Count);
        Assert.IsType<UpwindPhase>(phases[0]);
        Assert.IsType<TouchAndGoPhase>(phases[5]);
    }

    [Fact]
    public void UpdateWaypoints_UpdatesAllPatternPhases()
    {
        var rwy = DefaultRunway();
        var phaseList = new PhaseList { AssignedRunway = rwy };
        var oldWp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Left);
        var newWp = PatternGeometry.Compute(rwy, AircraftCategory.Jet, PatternDirection.Right);

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
