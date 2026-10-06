using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// A queued <see cref="CommandBlock"/> fires with the scripted flag of the dispatch that created it
/// (<see cref="DispatchContext.IsScenarioScripted"/>: a preset or an AI position, not the student) —
/// across a snapshot restore as well as live. <c>SimulationEngine.RehydrateRestoredQueueBlocks</c>
/// rebuilds every restored block's <c>ApplyAction</c> from a fresh context, and the flag the block
/// carried decides whether that rebuilt action counts as scripted: a preset queued behind a trigger
/// keeps firing as scripted, so it does not, for example, mark the speed assignment it carries as
/// controller-issued.
/// </summary>
public class QueuedBlockScriptedFlagRestoreTests
{
    private const string Callsign = "SWA100";
    private const string Fix = "SUNOL";

    /// <summary>An <c>AT</c> fix trigger with a trailing <c>WAIT</c>: the speed command is queued, not applied, at dispatch.</summary>
    private const string Command = $"AT {Fix} WAIT 5 SPD 250";

    private const int TickBudgetSeconds = 20;

    public QueuedBlockScriptedFlagRestoreTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState AirborneAtFix()
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition(Fix)!.Value;
        return new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = new LatLon(lat, lon),
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = 8000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            Transponder = new AircraftTransponder
            {
                Code = 4521,
                AssignedCode = 4521,
                Mode = "C",
            },
            FlightPlan = new AircraftFlightPlan
            {
                Destination = "OAK",
                FlightRules = "IFR",
                HasFlightPlan = true,
            },
            Track = new AircraftTrack(),
        };
    }

    private static SimulationEngine NewEngine()
    {
        return new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "queued-block-scripted-flag",
                ScenarioName = "Queued block scripted flag",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
    }

    private static CompoundCommand Parse(string command)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        Assert.True(parsed.IsSuccess, parsed.Reason);
        return parsed.Value!;
    }

    /// <summary>Queues <see cref="Command"/> on a fresh engine and hands back the block plus a snapshot of the queue.</summary>
    private static (CommandBlock Block, StateSnapshotDto Snapshot) DispatchQueuedSpeed(bool scripted)
    {
        AircraftState aircraft = AirborneAtFix();
        SimulationEngine engine = NewEngine();
        engine.World.AddAircraft(aircraft);

        CommandResult result = CommandDispatcher.DispatchCompound(
            Parse(Command),
            aircraft,
            engine.BuildDispatchContext(aircraft, scripted, facilityHint: null)
        );
        Assert.True(result.Success, result.Message);

        // Precondition, so neither test can pass vacuously: the block is queued behind its WAIT and the
        // speed it carries has not been applied.
        CommandBlock block = Assert.Single(aircraft.Queue.Blocks);
        Assert.True(block.IsWaitBlock, "test setup: the dispatched block should carry the WAIT");
        Assert.False(aircraft.Targets.HasExplicitSpeedCommand, "test setup: the speed command should still be queued");

        return (block, engine.CaptureSnapshot());
    }

    /// <summary>Restores <paramref name="snapshot"/> into a second engine and ticks until the queued speed block fires.</summary>
    private static AircraftState RestoreAndFire(StateSnapshotDto snapshot)
    {
        SimulationEngine restored = NewEngine();
        restored.RestoreFromSnapshot(snapshot);

        AircraftState aircraft = Assert.IsType<AircraftState>(restored.FindAircraft(Callsign));
        TickUntilSpeedApplied(restored, aircraft);
        return aircraft;
    }

    /// <summary>Ticks an engine — live or restored — until the queued speed block fires.</summary>
    private static void TickUntilSpeedApplied(SimulationEngine engine, AircraftState aircraft)
    {
        for (int second = 0; (second < TickBudgetSeconds) && !aircraft.Targets.HasExplicitSpeedCommand; second++)
        {
            engine.TickOneSecond();
        }

        Assert.True(aircraft.Targets.HasExplicitSpeedCommand, $"the queued speed block never fired (waited {TickBudgetSeconds}s)");
    }

    [Fact]
    public void ScriptedQueuedSpeedBlock_RestoredFromSnapshot_StaysScripted()
    {
        (CommandBlock block, StateSnapshotDto snapshot) = DispatchQueuedSpeed(scripted: true);
        Assert.True(block.IsScenarioScripted, "test setup: a preset dispatch should have marked its queued block scripted");

        AircraftState restored = RestoreAndFire(snapshot);

        Assert.False(
            restored.Targets.SpeedCommandIsControllerIssued,
            "the scripted queued block fired as student-issued after the restore, so its speed command claimed controller ownership"
        );
    }

    [Fact]
    public void ControllerQueuedSpeedBlock_RestoredFromSnapshot_StaysControllerIssued()
    {
        (CommandBlock block, StateSnapshotDto snapshot) = DispatchQueuedSpeed(scripted: false);
        Assert.False(block.IsScenarioScripted, "test setup: a student dispatch should have left its queued block unscripted");

        AircraftState restored = RestoreAndFire(snapshot);

        Assert.True(
            restored.Targets.SpeedCommandIsControllerIssued,
            "the student's queued speed block lost its controller provenance across the restore"
        );
    }

    /// <summary>
    /// A block the student's partial supersede split is rebuilt by <c>CommandDispatcher.SplitBlockNonConflicting</c>
    /// around the superseding dispatch's context, so its survivor is unscripted even though the block it came from was
    /// scripted — and that survivor fires the same way after a snapshot restore as it does live.
    /// </summary>
    [Fact]
    public void ScriptedBlockSplitByStudentDispatch_SurvivorRestoresAsStudentIssued()
    {
        AircraftState aircraft = AirborneAtFix();
        SimulationEngine live = NewEngine();
        live.World.AddAircraft(aircraft);

        // A scripted block holding a lateral and a speed command: the student's lateral supersede conflicts with the
        // lateral half only, so the split rebuilds the block around the surviving speed command.
        string scripted = $"AT {Fix} FH 270 SPD 210";
        CommandResult queued = CommandDispatcher.DispatchCompound(
            Parse(scripted),
            aircraft,
            live.BuildDispatchContext(aircraft, true, facilityHint: null)
        );
        Assert.True(queued.Success, queued.Message);
        CommandBlock original = Assert.Single(aircraft.Queue.Blocks);
        Assert.True(original.IsScenarioScripted, "test setup: the scripted dispatch should have marked its queued block scripted");

        CommandResult supersede = CommandDispatcher.DispatchCompound(
            Parse("FH 090"),
            aircraft,
            live.BuildDispatchContext(aircraft, false, facilityHint: null)
        );
        Assert.True(supersede.Success, supersede.Message);

        CommandBlock survivor = Assert.Single(aircraft.Queue.Blocks, b => b.Trigger is { Type: BlockTriggerType.ReachFix });
        Assert.NotSame(original, survivor);
        Assert.False(survivor.IsScenarioScripted, "the student's supersede should have left an unscripted survivor");

        StateSnapshotDto snapshot = live.CaptureSnapshot();

        // The same survivor fired twice: live on the engine that dispatched it, and after a restore.
        TickUntilSpeedApplied(live, aircraft);
        AircraftState afterRestore = RestoreAndFire(snapshot);

        Assert.True(
            aircraft.Targets.SpeedCommandIsControllerIssued,
            "the unscripted survivor's live firing did not claim the speed, so the probe is not measuring the student's dispatch"
        );
        Assert.Equal(aircraft.Targets.SpeedCommandIsControllerIssued, afterRestore.Targets.SpeedCommandIsControllerIssued);
    }

    /// <summary>
    /// A snapshot written before the flag existed has no <c>IsScenarioScripted</c> key at all, so the DTO
    /// must read it as false: the block restores as student-issued.
    /// </summary>
    [Fact]
    public void CommandBlockDto_WithoutScriptedFlag_ReadsAsNotScripted()
    {
        const string OldSnapshotJson = """
            {
              "Commands": [],
              "IsApplied": false,
              "TriggerMet": false,
              "TriggerClosestApproach": 0,
              "TriggerMissed": false,
              "IsWaitBlock": false,
              "WaitRemainingSeconds": 0,
              "WaitRemainingDistanceNm": 0,
              "Description": "SPD 250",
              "NaturalDescription": "reduce to 250"
            }
            """;

        CommandBlockDto dto = Assert.IsType<CommandBlockDto>(
            JsonSerializer.Deserialize<CommandBlockDto>(OldSnapshotJson, RecordingJsonOptions.Default)
        );

        var block = CommandBlock.FromSnapshot(dto);

        Assert.False(block.IsScenarioScripted);
    }
}
