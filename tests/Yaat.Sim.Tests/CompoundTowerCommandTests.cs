using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

/// <summary>
/// E2E tests for compound commands starting with tower commands (CTO, LUAW)
/// followed by post-departure blocks (DCT, FH, CM, etc.).
/// Verifies that blocks after `;` are enqueued and execute after phases complete.
/// </summary>
public class CompoundTowerCommandTests
{
    private readonly ITestOutputHelper _output;

    public CompoundTowerCommandTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static RunwayInfo Oak28R() =>
        TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );

    private static AircraftState MakeLinedUpAircraft(RunwayInfo runway)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            Heading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            Departure = "OAK",
            CruiseAltitude = 5000,
        };

        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new LinedUpAndWaitingPhase());
        phases.Add(new TakeoffPhase());
        phases.Add(new InitialClimbPhase { Departure = new DefaultDeparture(), CruiseAltitude = 5000 });

        ac.Phases = phases;
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));

        return ac;
    }

    private static PhaseContext MakePhaseContext(AircraftState ac, double delta = 1.0)
    {
        var runway = ac.Phases?.AssignedRunway;
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = delta,
            Runway = runway,
            FieldElevation = runway?.ElevationFt ?? 0,
            Logger = NullLogger.Instance,
        };
    }

    /// <summary>
    /// CTO MR270; DCT SUNOL — the DCT block must be enqueued after the
    /// tower command (CTO) is processed. Currently the DCT block is silently
    /// dropped because DispatchWithPhase returns early.
    /// </summary>
    [Fact]
    public void Cto_Semicolon_Dct_EnqueuesPostDepartureBlock()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        NavigationDatabase.SetInstance(navDb);
        TestVnasData.EnsureInitialized();

        var runway = Oak28R();
        var ac = MakeLinedUpAircraft(runway);

        // Parse the compound command
        var parseResult = CommandParser.ParseCompound("CTO MR270; DCT SUNOL", ac.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");
        Assert.Equal(2, parseResult.Value!.Blocks.Count);

        // Dispatch the compound command
        var result = CommandDispatcher.DispatchCompound(parseResult.Value, ac, null, Random.Shared, false);

        Assert.True(result.Success, $"Dispatch failed: {result.Message}");

        // The DCT block must be enqueued in the command queue
        Assert.True(ac.Queue.Blocks.Count > 0, "DCT block was not enqueued — remaining compound blocks after tower command were dropped");

        // The queue should not be complete yet (DCT hasn't executed)
        Assert.False(ac.Queue.IsComplete, "Queue should not be complete before phases finish");
    }

    /// <summary>
    /// Full E2E: CTO MR270; DCT SUNOL — aircraft takes off, turns right 270°,
    /// climbs through InitialClimb, then after phases complete the DCT block
    /// executes and sets SUNOL as navigation target.
    /// Logs position/heading/phase each tick for diagnostics.
    /// </summary>
    [Fact]
    public void Cto_MR270_Dct_ExecutesAfterPhasesComplete()
    {
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        NavigationDatabase.SetInstance(navDb);
        TestVnasData.EnsureInitialized();

        var runway = Oak28R();
        var ac = MakeLinedUpAircraft(runway);

        // Parse and dispatch
        var parseResult = CommandParser.ParseCompound("CTO MR270; DCT SUNOL", ac.Route);
        Assert.True(parseResult.IsSuccess, $"Parse failed: {parseResult.Reason}");

        var result = CommandDispatcher.DispatchCompound(parseResult.Value!, ac, null, Random.Shared, false);
        Assert.True(result.Success, $"Dispatch failed: {result.Message}");

        // Tick the simulation: PhaseRunner then FlightPhysics per tick
        bool phasesCompleted = false;
        bool dctApplied = false;
        int maxTicks = 600;

        for (int tick = 0; tick < maxTicks; tick++)
        {
            // PhaseRunner first (matches SimulationEngine order)
            var ctx = MakePhaseContext(ac);
            PhaseRunner.Tick(ac, ctx);

            // FlightPhysics (includes UpdateCommandQueue)
            FlightPhysics.Update(ac, 1.0);

            // Log every 10 ticks or on key events
            string phaseName = ac.Phases?.CurrentPhase?.Name ?? (ac.Phases?.IsComplete == true ? "COMPLETE" : "NONE");
            bool isPhaseTransition = tick == 0 || (tick > 0 && phaseName != (ac.Phases?.CurrentPhase?.Name ?? ""));

            if (tick % 10 == 0 || isPhaseTransition || !phasesCompleted)
            {
                _output.WriteLine(
                    $"t={tick, 4}: phase={phaseName, -20} hdg={ac.Heading:F0} alt={ac.Altitude:F0} ias={ac.IndicatedAirspeed:F0} "
                        + $"onGround={ac.IsOnGround} navRoute={ac.Targets.NavigationRoute.Count} queueBlocks={ac.Queue.Blocks.Count} "
                        + $"queueIdx={ac.Queue.CurrentBlockIndex} queueDone={ac.Queue.IsComplete}"
                );
            }

            // Detect when phases complete
            if (!phasesCompleted && ac.Phases?.CurrentPhase is null)
            {
                phasesCompleted = true;
                _output.WriteLine($"*** Phases completed at tick {tick} ***");
            }

            // Detect when DCT block applies (navigation route gets SUNOL)
            if (phasesCompleted && !dctApplied && ac.Targets.NavigationRoute.Count > 0)
            {
                dctApplied = true;
                _output.WriteLine($"*** DCT applied at tick {tick}: route=[{string.Join(", ", ac.Targets.NavigationRoute.Select(r => r.Name))}] ***");
            }

            if (dctApplied)
            {
                break;
            }
        }

        // Assertions
        Assert.True(phasesCompleted, "Phases should have completed (InitialClimb departure heading reached)");
        Assert.True(dctApplied, "DCT SUNOL should have been applied after phases completed");
        Assert.Contains(ac.Targets.NavigationRoute, t => t.Name == "SUNOL");
    }
}
