using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A hold-for-release ground departure released and then lined up by the controller waits for a takeoff clearance the
/// controller gives: the automatic takeoff clearance after a release is only for an aircraft holding short of its
/// departure runway, or for a runway spawn that asked the radar student for its release.
/// </summary>
[Collection("NavDbMutator")]
public class ReleasedDepartureLineUpE2ETests
{
    public ReleasedDepartureLineUpE2ETests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void HfrGroundDeparture_ReleasedAtTheBarThenLinedUp_WaitsForAManualTakeoffClearance()
    {
        if (LoadHeldAtTheBar(solo: true, ScenarioJson()) is not { } run)
        {
            return;
        }

        Assert.True(HeldReleaseService.Release(run.Engine.Scenario!, run.Engine.World, run.Engine.World.Rng, "SWA1234", null).Success);
        CommandResult lineUp = run.Engine.SendCommand("SWA1234", "LUAW");
        Assert.True(lineUp.Success, lineUp.Message);
        Assert.True(TickUntil(run.Engine, () => run.Aircraft.Phases?.CurrentPhase is LinedUpAndWaitingPhase, 120) > 0, "never lined up");

        Tick(run.Engine, 30);

        LinedUpAndWaitingPhase linedUp = Assert.IsType<LinedUpAndWaitingPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.False(linedUp.HasTakeoffClearance, "no takeoff clearance was given");
        Assert.True(run.Aircraft.IsOnGround);
    }

    [Fact]
    public void HfrGroundDeparture_TimedPresetTaxiThenCto_DispatchesTheTaxiOnTime()
    {
        if (LoadHeld(solo: false, ScenarioJson("""{ "id": "p0", "command": "TAXIAUTO 28R; CTO", "timeOffset": 30 }""")) is not { } run)
        {
            return;
        }

        Tick(run.Engine, 32);

        Assert.True(run.Aircraft.Ground.HeldForRelease);
        Assert.Empty(run.Engine.Scenario!.PresetQueue);
        Assert.True(run.Aircraft.Ground.AssignedTaxiRoute is not null, string.Join(" | ", run.Terminal.Select(t => t.Message)));
    }

    [Fact]
    public void HfrGroundDeparture_SoloRoom_TimedPresetCto_TakesOnlyThePresetClearance_AndClearsTheReleaseFlag()
    {
        if (LoadHeldAtTheBar(solo: true, ScenarioJson("""{ "id": "p0", "command": "CTO", "timeOffset": 30 }""")) is not { } run)
        {
            return;
        }

        SimScenarioState scenario = run.Engine.Scenario!;
        Assert.Single(scenario.PresetQueue, p => p.Command == "CTO");
        Assert.True(HeldReleaseService.Release(scenario, run.Engine.World, run.Engine.World.Rng, "SWA1234", null).Success);

        Assert.True(TickUntil(run.Engine, () => !run.Aircraft.IsOnGround, 180) > 0, "the preset CTO never fired after the release");
        Tick(run.Engine, 30);

        Assert.False(run.Aircraft.Ground.ReleasedForDeparture, "the release flag outlived the preset takeoff clearance");
        Assert.Single(run.Terminal, t => t.Message.Contains("[Preset] CTO", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Terminal, t => t.Message.Contains("[HFR] Released", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfrGroundDeparture_ReleasedAtTheBar_TakesTheAutomaticTakeoffInASoloRoomOnly(bool solo)
    {
        if (LoadHeldAtTheBar(solo, ScenarioJson()) is not { } run)
        {
            return;
        }

        Assert.True(HeldReleaseService.Release(run.Engine.Scenario!, run.Engine.World, run.Engine.World.Rng, "SWA1234", null).Success);

        int departed = TickUntil(run.Engine, () => run.Aircraft.Phases?.CurrentPhase is not HoldingShortPhase, 120);

        if (solo)
        {
            Assert.True(departed > 0, "the released solo departure was never cleared for takeoff");
            return;
        }

        Assert.Equal(-1, departed);
        Assert.True(run.Aircraft.IsOnGround);
        Assert.False(run.Aircraft.Ground.HeldForRelease);
        Assert.False(run.Aircraft.Ground.ReleasedForDeparture, "the released departure's automatic takeoff is still pending");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HfrGroundDeparture_RpoRoom_TimedPresetCto_IsHeldUntilTheRelease_ThenFires(bool releaseByHfrOff)
    {
        if (LoadHeldAtTheBar(solo: false, ScenarioJson("""{ "id": "p0", "command": "CTO", "timeOffset": 30 }""")) is not { } run)
        {
            return;
        }

        SimScenarioState scenario = run.Engine.Scenario!;
        Assert.True(scenario.ElapsedSeconds > 30, "the preset CTO fell due after the aircraft reached the bar");
        Tick(run.Engine, 60);
        Assert.IsType<HoldingShortPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.Single(scenario.PresetQueue, p => p.Command == "CTO");

        HeldReleaseResult released = releaseByHfrOff
            ? HeldReleaseService.Disarm(scenario, run.Engine.World, "KOAK")
            : HeldReleaseService.Release(scenario, run.Engine.World, run.Engine.World.Rng, "SWA1234", null);
        Assert.True(released.Success, released.Message);

        Assert.True(TickUntil(run.Engine, () => !run.Aircraft.IsOnGround, 180) > 0, "the preset CTO never fired after the release");
        Assert.Empty(scenario.PresetQueue);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HeldDeparture_ChainedPresetCto_WaitsForReleaseThenDeparts(bool solo, bool releaseByHfrOff)
    {
        if (LoadHeldWithChainedCtoAtTheBar(solo) is not { } run)
        {
            return;
        }

        Assert.True(run.Aircraft.Ground.HeldForRelease);
        Assert.IsType<HoldingShortPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.True(run.Aircraft.IsOnGround);
        Assert.Null(run.Aircraft.Phases.DepartureClearance);
        Assert.Single(run.Aircraft.Queue.Blocks, IsPendingTakeoffClearance);
        Assert.Single(run.Terminal, t => t.Message == "SWA1234 CTO waits for the release");

        SimScenarioState scenario = run.Engine.Scenario!;
        HeldReleaseResult released = releaseByHfrOff
            ? HeldReleaseService.Disarm(scenario, run.Engine.World, "KOAK")
            : HeldReleaseService.Release(scenario, run.Engine.World, run.Engine.World.Rng, "SWA1234", null);
        Assert.True(released.Success, released.Message);

        Assert.True(TickUntil(run.Engine, () => !run.Aircraft.IsOnGround, 180) > 0, "the chained preset CTO never fired after the release");
        Tick(run.Engine, 30);

        Assert.DoesNotContain(run.Aircraft.Queue.Blocks, IsPendingTakeoffClearance);
        Assert.DoesNotContain(run.Terminal, t => t.Message.Contains("[HFR] Released", StringComparison.Ordinal));
        Assert.Single(run.Terminal, t => t.Message.Contains("waits for the release", StringComparison.Ordinal));
        Assert.False(run.Aircraft.Ground.ReleasedForDeparture, "the release's automatic takeoff is still pending after the preset CTO");
    }

    [Fact]
    public void HeldDeparture_WaitingChainedCto_SurvivesASnapshotRestore_ThenDepartsOnce()
    {
        if (LoadHeldWithChainedCtoAtTheBar(solo: false) is not { } run)
        {
            return;
        }

        string json = JsonSerializer.Serialize(run.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        run.Engine.RestoreFromSnapshot(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!);
        AircraftState aircraft = run.Engine.FindAircraft("SWA1234") ?? throw new InvalidOperationException("SWA1234 lost in the restore");
        Tick(run.Engine, 5);

        Assert.True(aircraft.Ground.HeldForRelease);
        Assert.IsType<HoldingShortPhase>(aircraft.Phases!.CurrentPhase);
        Assert.Single(aircraft.Queue.Blocks, IsPendingTakeoffClearance);

        SimScenarioState scenario = run.Engine.Scenario!;
        HeldReleaseResult released = HeldReleaseService.Release(scenario, run.Engine.World, run.Engine.World.Rng, "SWA1234", null);
        Assert.True(released.Success, released.Message);

        Assert.True(TickUntil(run.Engine, () => !aircraft.IsOnGround, 180) > 0, "the restored chained CTO never fired after the release");
        Tick(run.Engine, 30);

        Assert.DoesNotContain(aircraft.Queue.Blocks, IsPendingTakeoffClearance);
        Assert.DoesNotContain(run.Terminal, t => t.Message.Contains("[HFR] Released", StringComparison.Ordinal));
    }

    [Fact]
    public void HeldDeparture_WaitingTriggeredCto_LetsALaterTriggeredBlockFire()
    {
        if (LoadHeldAtTheBar(solo: false, ScenarioJson()) is not { } run)
        {
            return;
        }

        CommandResult cto = run.Engine.SendCommand("SWA1234", "AT B CTO");
        Assert.True(cto.Success, cto.Message);
        CommandResult squawk = run.Engine.SendCommand("SWA1234", "AT B SQ 1234");
        Assert.True(squawk.Success, squawk.Message);

        FlightPhysics.NotifyGroundEntityReached(run.Aircraft, arrivedNodeId: null, newTaxiwayName: "B");
        Tick(run.Engine, 5);

        Assert.Equal(1234u, run.Aircraft.Transponder.Code);
        Assert.Single(run.Aircraft.Queue.Blocks, IsPendingTakeoffClearance);
        Assert.IsType<HoldingShortPhase>(run.Aircraft.Phases!.CurrentPhase);

        HeldReleaseResult released = HeldReleaseService.Release(run.Engine.Scenario!, run.Engine.World, run.Engine.World.Rng, "SWA1234", null);
        Assert.True(released.Success, released.Message);
        Assert.True(TickUntil(run.Engine, () => !run.Aircraft.IsOnGround, 180) > 0, "the triggered CTO never fired after the release");
    }

    [Fact]
    public void HeldDeparture_TimedPresetsLuawThenCto_DispatchInScenarioOrderOnTheRelease()
    {
        string presets = """
            { "id": "p0", "command": "LUAW", "timeOffset": 30 }, { "id": "p1", "command": "CTO", "timeOffset": 60 }
            """;
        if (LoadHeldAtTheBar(solo: false, ScenarioJson(presets)) is not { } run)
        {
            return;
        }

        SimScenarioState scenario = run.Engine.Scenario!;
        Assert.True(TickUntil(run.Engine, () => scenario.ElapsedSeconds > 61, 120) > 0);
        Tick(run.Engine, 5);
        Assert.Equal(2, scenario.PresetQueue.Count);
        Assert.IsType<HoldingShortPhase>(run.Aircraft.Phases!.CurrentPhase);

        HeldReleaseResult released = HeldReleaseService.Release(scenario, run.Engine.World, run.Engine.World.Rng, "SWA1234", null);
        Assert.True(released.Success, released.Message);
        Assert.True(TickUntil(run.Engine, () => !run.Aircraft.IsOnGround, 180) > 0, "the held presets never took the departure off");

        List<string> dispatched = [.. run.Terminal.Where(t => t.Message.StartsWith("[Preset] ", StringComparison.Ordinal)).Select(t => t.Message)];
        Assert.Equal(["[Preset] LUAW", "[Preset] CTO"], dispatched);
        Assert.DoesNotContain(run.Terminal, t => (t.Kind == "Warning") && t.Message.Contains("[Preset]", StringComparison.Ordinal));
        Assert.Empty(scenario.PresetQueue);
    }

    private static bool IsPendingTakeoffClearance(CommandBlock block) =>
        !block.IsApplied && (block.ParsedCommands?.Any(c => c is ClearedForTakeoffCommand) ?? false);

    /// <summary>
    /// Loads SWA1234 held for release with the timed preset <c>TAXIAUTO 28R; CTO</c>, ticks until the taxi brings it to the bar
    /// of 28R, and holds it there 30 s with the chained CTO due.
    /// </summary>
    private static Run? LoadHeldWithChainedCtoAtTheBar(bool solo)
    {
        if (LoadHeld(solo, ScenarioJson("""{ "id": "p0", "command": "TAXIAUTO 28R; CTO", "timeOffset": 30 }""")) is not { } run)
        {
            return null;
        }

        Assert.True(
            TickUntil(
                run.Engine,
                () => run.Aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway },
                900
            ) > 0,
            "never reached the bar of its departure runway"
        );
        Tick(run.Engine, 30);
        return run;
    }

    private sealed record Run(SimulationEngine Engine, AircraftState Aircraft, List<TerminalEntry> Terminal);

    /// <summary>Loads SWA1234 on its OAK stand and arms hold for release there, so it is held.</summary>
    private static Run? LoadHeld(bool solo, string scenarioJson)
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => terminal.Add(entry);
        List<string> warnings = engine.LoadScenario(scenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = solo;
        engine.TickOneSecond();
        AircraftState aircraft = engine.FindAircraft("SWA1234") ?? throw new InvalidOperationException("SWA1234 did not spawn");

        Assert.True(HeldReleaseService.Arm(scenario, engine.World, "KOAK").Success);
        Assert.True(aircraft.Ground.HeldForRelease);
        return new Run(engine, aircraft, terminal);
    }

    /// <summary>Loads SWA1234 held for release at OAK (<see cref="LoadHeld"/>) and taxis it to the bar of 28R, where it holds.</summary>
    private static Run? LoadHeldAtTheBar(bool solo, string scenarioJson)
    {
        if (LoadHeld(solo, scenarioJson) is not { } run)
        {
            return null;
        }

        CommandResult taxi = run.Engine.SendCommand("SWA1234", "TAXIAUTO 28R");
        Assert.True(taxi.Success, taxi.Message);
        Assert.True(
            TickUntil(
                run.Engine,
                () => run.Aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway },
                900
            ) > 0,
            "never reached the bar of its departure runway"
        );
        return run;
    }

    private static string ScenarioJson() => ScenarioJson("");

    private static string ScenarioJson(string presetJson) =>
        $$"""
            {
              "id": "released-departure-luaw",
              "name": "Released departure lined up",
              "artccId": "ZOA",
              "primaryAirportId": "OAK",
              "aircraft": [
                {
                  "id": "a1",
                  "aircraftId": "SWA1234",
                  "aircraftType": "B738",
                  "transponderMode": "C",
                  "startingConditions": { "type": "Parking", "parking": "SIG4" },
                  "flightplan": {
                    "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450,
                    "route": "", "remarks": "", "aircraftType": "B738"
                  },
                  "presetCommands": [ {{presetJson}} ]
                }
              ]
            }
            """;

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    private static int TickUntil(SimulationEngine engine, Func<bool> done, int maxSeconds)
    {
        for (int i = 1; i <= maxSeconds; i++)
        {
            engine.TickOneSecond();
            if (done())
            {
                return i;
            }
        }

        return -1;
    }
}
