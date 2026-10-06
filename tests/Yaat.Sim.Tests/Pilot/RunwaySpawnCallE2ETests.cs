using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// What a runway spawn says at its lined-up call point (YAAT-308): a SAY-only spawn stays silent; a spawn with no preset
/// makes today's lined-up call to a tower student, departs on an automatic takeoff clearance under a radar student at a
/// towered field (FAT, which the ZOA config gives a cab), and, when IFR, asks a radar student for its release at an
/// untowered one (AUN, which it does not) a few seconds after lining up; a VFR spawn there departs on its own. Real spawns
/// from the ZOA inventory: Y-KG FAT PTACs (AAL1828 B738 on FAT 29R) and S3-BAY-2 (N513SJ C421 on AUN 25).
/// </summary>
[Collection("NavDbMutator")]
public class RunwaySpawnCallE2ETests
{
    private static readonly Spawn Fat = new("FAT", "29R", "AAL1828", "B738", "IFR", 0);
    private static readonly Spawn Aun = new("AUN", "25", "N513SJ", "C421", "IFR", 0);

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public RunwaySpawnCallE2ETests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void IsTowered_TrueForAFieldWithACab_FalseForOneWithout()
    {
        if (_zoa is null)
        {
            return;
        }

        Assert.True(AiPositionResolver.IsTowered(_zoa, "FAT"));
        Assert.True(AiPositionResolver.IsTowered(_zoa, "KOAK"));
        Assert.False(AiPositionResolver.IsTowered(_zoa, "AUN"));
    }

    [Fact]
    public void NoPreset_TowerStudent_MakesTodaysLinedUpCall()
    {
        if (Load(Fat, "FAT_TWR", "TWR", []) is not { } run)
        {
            return;
        }

        Tick(run, 100);
        TerminalEntry call = Assert.Single(run.Terminal, t => t.Message.Contains("runway 29R, ready.", StringComparison.Ordinal));
        Assert.Equal(PilotPendingRequestKind.Takeoff, run.Aircraft.PendingPilotRequest!.Kind);
        Assert.DoesNotContain("release", call.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPreset_TowerStudent_DecisionClosedAfterTakeoff()
    {
        if (Load(Fat, "FAT_TWR", "TWR", []) is not { } run)
        {
            return;
        }

        Tick(run, 100);
        CommandResult cleared = run.Engine.SendCommand("AAL1828", "CTO");
        Assert.True(cleared.Success, cleared.Message);
        Assert.True(TickUntil(run, () => !run.Aircraft.IsOnGround, 120) > 0, "never took off");

        Assert.True(run.Aircraft.Ground.InitialCallupDecisionProcessed);
    }

    [Fact]
    public void SayOnly_NeverMakesTheLinedUpCall_AndTheSayIsSpoken()
    {
        if (Load(Aun, "FAT_F_APP", "APP", [("SAY READY FOR RELEASE", 0)]) is not { } run)
        {
            return;
        }

        Tick(run, 300);

        Assert.Contains(run.Terminal, e => e.Message.Contains("READY FOR RELEASE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(run.Terminal, e => e.Kind == "SayPilot");
        Assert.Null(run.Aircraft.PendingPilotRequest);
        Assert.IsType<LinedUpAndWaitingPhase>(run.Aircraft.Phases!.CurrentPhase);
    }

    [Fact]
    public void NoPreset_ApproachStudent_TowerField_TakesOffOnItsOwnAndChecksInAirborne()
    {
        if (Load(Fat, "FAT_F_APP", "APP", []) is not { } run)
        {
            return;
        }

        Tick(run, 85);
        Assert.IsType<LinedUpAndWaitingPhase>(run.Aircraft.Phases!.CurrentPhase);

        int airborneAfter = TickUntil(run, () => !run.Aircraft.IsOnGround, 120);
        Assert.True(airborneAfter > 0, "never took off");
        Assert.DoesNotContain(run.Terminal, t => t.Message.Contains("ready.", StringComparison.Ordinal));
        Assert.Empty(ReleaseLines(run));

        Assert.True(
            run.Aircraft.HasMadeInitialContact || (TickUntil(run, () => run.Aircraft.HasMadeInitialContact, 180) > 0),
            "no airborne check-in"
        );
        Assert.Contains(run.Terminal, e => e.Kind == "SayPilot");
    }

    [Fact]
    public void NoPreset_ApproachStudent_TowerField_RpoRoom_WaitsLinedUpForTheRpoTakeoffClearance()
    {
        if (Prepare(Fat, "FAT_F_APP", "APP", []) is not { } prepared)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> terminal) = prepared;
        engine.Scenario!.SoloTrainingMode = false;

        Tick(engine, 120);

        AircraftState aircraft = engine.FindAircraft(Fat.Callsign) ?? throw new InvalidOperationException($"{Fat.Callsign} did not spawn");
        AssertWaitsLinedUpForTheRpo(aircraft, terminal);
    }

    [Fact]
    public void NoPreset_ApproachStudent_UntoweredField_Vfr_RpoRoom_WaitsLinedUpForTheRpoTakeoffClearance()
    {
        if (Prepare(Aun with { Rules = "VFR" }, "FAT_F_APP", "APP", []) is not { } prepared)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> terminal) = prepared;
        engine.Scenario!.SoloTrainingMode = false;

        Tick(engine, 240);

        AircraftState aircraft = engine.FindAircraft(Aun.Callsign) ?? throw new InvalidOperationException($"{Aun.Callsign} did not spawn");
        AssertWaitsLinedUpForTheRpo(aircraft, terminal);
    }

    [Fact]
    public void HfrGatedRunwaySpawn_UntoweredField_RpoRoom_ReleasedThroughTheGate_WaitsLinedUpForTheRpoTakeoffClearance()
    {
        if (Prepare(Aun with { DelaySeconds = 10 }, "FAT_F_APP", "APP", []) is not { } prepared)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> terminal) = prepared;
        engine.Scenario!.SoloTrainingMode = false;
        AircraftState aircraft = ReleaseThroughTheGate(engine, "KAUN", "N513SJ");

        Tick(engine, 150);

        AssertWaitsLinedUpForTheRpo(aircraft, terminal);
        Assert.False(aircraft.Ground.ReleasedForDeparture, "the released departure's automatic takeoff is still pending");
    }

    /// <summary>
    /// In an RPO room nothing clears a runway spawn for takeoff: it stays lined up with no clearance, its call-up decision is
    /// closed without a lined-up call, so the RPO's own takeoff clearance is what launches it.
    /// </summary>
    private static void AssertWaitsLinedUpForTheRpo(AircraftState aircraft, List<TerminalEntry> terminal)
    {
        Assert.IsType<LinedUpAndWaitingPhase>(aircraft.Phases!.CurrentPhase);
        Assert.False(HasTakeoffClearance(aircraft), "an RPO room's runway spawn was cleared for takeoff automatically");
        Assert.True(aircraft.IsOnGround);
        Assert.True(aircraft.Ground.InitialCallupDecisionProcessed, "the call-up decision is still open");
        Assert.DoesNotContain(terminal, t => t.Message.Contains("[Auto]", StringComparison.Ordinal));
        Assert.DoesNotContain(terminal, t => t.Message.Contains("ready.", StringComparison.Ordinal));
    }

    [Fact]
    public void NoPreset_ApproachStudent_UntoweredField_RequestsReleasePromptly_DepartsOnRelease_AndChecksInAirborne()
    {
        if (Load(Aun, "FAT_F_APP", "APP", []) is not { } run)
        {
            return;
        }

        Assert.True(TickUntil(run, () => ReleaseLines(run).Count > 0, 30) > 0, "no release request");
        double linedUpFor = Assert.IsType<LinedUpAndWaitingPhase>(run.Aircraft.Phases!.CurrentPhase).ElapsedSeconds;
        Assert.InRange(linedUpFor, 5.0, 11.0);

        TerminalEntry request = Assert.Single(run.Terminal);
        Assert.Contains("runway 25 at Auburn", request.Message, StringComparison.Ordinal);
        Assert.EndsWith("ready for departure, request release.", request.Message, StringComparison.Ordinal);
        Assert.Equal(PilotPendingRequestKind.Release, run.Aircraft.PendingPilotRequest!.Kind);
        Assert.True(run.Aircraft.Ground.HeldForRelease);
        Assert.False(run.Aircraft.HasMadeInitialContact, "a release request is not the initial contact");
        Assert.Equal("N513SJ", run.Engine.World.ActiveFrequency.AwaitingControllerResponseTo);

        CommandResult released = run.Engine.SendCommand("N513SJ", "REL N513SJ");
        Assert.True(released.Success, released.Message);
        Assert.False(run.Aircraft.PendingPilotRequest!.IsOpen);
        Assert.Null(run.Engine.World.ActiveFrequency.AwaitingControllerResponseTo);

        int saidBeforeTakeoff = run.Terminal.Count;
        Assert.True(TickUntil(run, () => !run.Aircraft.IsOnGround, 120) > 0, "never departed after the release");
        AssertChecksInAirborne(run, saidBeforeTakeoff);
    }

    [Fact]
    public void NoPreset_ApproachStudent_UntoweredField_FollowsUpWithNoRelease()
    {
        if (Load(Aun, "FAT_F_APP", "APP", []) is not { } run)
        {
            return;
        }

        Tick(run, 11 + (int)PilotRequestTracker.NormalFollowUpDelaySeconds + 5);

        Assert.Equal(2, ReleaseLines(run).Count);
        Assert.IsType<LinedUpAndWaitingPhase>(run.Aircraft.Phases!.CurrentPhase);
    }

    [Fact]
    public void NoPreset_ApproachStudent_UntoweredField_Vfr_DepartsOnItsOwn_AndChecksInAirborne()
    {
        if (Load(Aun with { Rules = "VFR" }, "FAT_F_APP", "APP", []) is not { } run)
        {
            return;
        }

        Assert.True(TickUntil(run, () => !run.Aircraft.IsOnGround, 240) > 0, "never took off");
        Assert.Empty(ReleaseLines(run));
        Assert.False(run.Aircraft.Ground.HeldForRelease);

        AssertChecksInAirborne(run, 0);
    }

    [Fact]
    public void NoPreset_CenterStudent_UntoweredField_AddressesCenter_WhenNoCallNameIsConfigured()
    {
        if (Load(Aun, "OAK_14_CTR", "CTR", []) is not { } run)
        {
            return;
        }

        // A position the config does not know has no radio name, so the call falls back to the generic word.
        SimScenarioState scenario = run.Engine.Scenario!;
        scenario.StudentPosition = scenario.StudentPosition! with { Callsign = "ZZZ_99_CTR" };

        Assert.True(TickUntil(run, () => ReleaseLines(run).Count > 0, 30) > 0, "no release request");
        Assert.StartsWith("center, runway 25 at Auburn", Assert.Single(ReleaseLines(run)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPreset_ApproachStudent_UntoweredField_HfrOffAnswersTheReleaseRequest_NoFollowUp_AndDeparts()
    {
        if (Load(Aun, "FAT_F_APP", "APP", []) is not { } run)
        {
            return;
        }

        SimScenarioState scenario = run.Engine.Scenario!;
        Assert.True(HeldReleaseService.Arm(scenario, run.Engine.World, "KAUN").Success);
        Assert.True(TickUntil(run, () => ReleaseLines(run).Count > 0, 30) > 0, "no release request");

        Assert.True(HeldReleaseService.Disarm(scenario, run.Engine.World, "KAUN").Success);
        Assert.False(run.Aircraft.PendingPilotRequest!.IsOpen, "HFROFF is a release: it answers the request");

        Tick(run, (int)PilotRequestTracker.NormalFollowUpDelaySeconds + 5);

        Assert.Single(ReleaseLines(run));
        Assert.False(run.Aircraft.IsOnGround, "never departed after HFROFF");
    }

    [Fact]
    public void HfrGatedRunwaySpawn_UntoweredField_ReleasedThroughTheGate_DepartsOnItsOwn_WithNoReleaseRequest()
    {
        if (Prepare(Aun with { DelaySeconds = 10 }, "FAT_F_APP", "APP", []) is not { } prepared)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> terminal) = prepared;
        AircraftState aircraft = ReleaseThroughTheGate(engine, "KAUN", "N513SJ");
        Assert.IsType<LinedUpAndWaitingPhase>(aircraft.Phases!.CurrentPhase);

        // As one that asked and got REL: the line-up-and-wait auto-clearance after the 5 to 20 s tower jitter.
        Assert.InRange(TickUntil(engine, () => HasTakeoffClearance(aircraft), 30), 1, 30);
        Assert.True(TickUntil(engine, () => !aircraft.IsOnGround, 120) > 0, "never departed");
        Assert.DoesNotContain(terminal, t => t.Message.Contains("request release", StringComparison.Ordinal));
        Assert.False(aircraft.PendingPilotRequest is { Kind: PilotPendingRequestKind.Release });
        Assert.True(aircraft.Ground.InitialCallupDecisionProcessed);
    }

    [Fact]
    public void HfrGatedRunwaySpawn_ToweredField_ApproachStudent_TakesTheToweredAutoTakeoff()
    {
        if (Prepare(Fat with { DelaySeconds = 10 }, "FAT_F_APP", "APP", []) is not { } prepared)
        {
            return;
        }

        (SimulationEngine engine, List<TerminalEntry> terminal) = prepared;
        AircraftState aircraft = ReleaseThroughTheGate(engine, "KFAT", "AAL1828");

        // The simulated tower clears it at its lined-up call point, not on the release's tower jitter.
        Tick(engine, 80);
        Assert.False(HasTakeoffClearance(aircraft));
        Assert.True(TickUntil(engine, () => !aircraft.IsOnGround, 150) > 0, "never took off");
        Assert.DoesNotContain(terminal, t => t.Message.Contains("request release", StringComparison.Ordinal));
        Assert.DoesNotContain(terminal, t => t.Message.Contains("ready.", StringComparison.Ordinal));
    }

    /// <summary>Arms <paramref name="airport"/>, holds the delayed spawn at the gate, releases it with REL and returns it once it appears.</summary>
    private static AircraftState ReleaseThroughTheGate(SimulationEngine engine, string airport, string callsign)
    {
        SimScenarioState scenario = engine.Scenario!;
        Assert.True(HeldReleaseService.Arm(scenario, engine.World, airport).Success);
        Tick(engine, 40);
        Assert.Null(engine.FindAircraft(callsign));

        HeldReleaseResult released = HeldReleaseService.Release(scenario, engine.World, engine.World.Rng, callsign, null);
        Assert.True(released.Success, released.Message);
        Assert.True(TickUntil(engine, () => engine.FindAircraft(callsign) is not null, 90) > 0, "the released spawn never appeared");
        return engine.FindAircraft(callsign)!;
    }

    /// <summary>True once the aircraft has a takeoff clearance, or has left its lined-up position on one.</summary>
    private static bool HasTakeoffClearance(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase is not LinedUpAndWaitingPhase { HasTakeoffClearance: false };

    private sealed record Spawn(string Airport, string Runway, string Callsign, string Type, string Rules, int DelaySeconds);

    private sealed record Run(SimulationEngine Engine, AircraftState Aircraft, List<TerminalEntry> Terminal);

    private static List<TerminalEntry> ReleaseLines(Run run) =>
        [.. run.Terminal.Where(t => t.Message.Contains("request release", StringComparison.Ordinal))];

    /// <summary>The airborne check-in to the student follows the takeoff: the pilot makes its initial contact and speaks.</summary>
    private static void AssertChecksInAirborne(Run run, int saidBeforeTakeoff)
    {
        Assert.True(
            run.Aircraft.HasMadeInitialContact || (TickUntil(run, () => run.Aircraft.HasMadeInitialContact, 180) > 0),
            "no airborne check-in"
        );
        Assert.True(
            TickUntil(run, () => run.Terminal.Skip(saidBeforeTakeoff).Any(e => e.Kind == "SayPilot"), 60) is not -1,
            "the airborne check-in was never spoken"
        );
    }

    private Run? Load(Spawn spawn, string studentCallsign, string studentType, (string Command, int Offset)[] presets)
    {
        if (Prepare(spawn, studentCallsign, studentType, presets) is not { } prepared)
        {
            return null;
        }

        (SimulationEngine engine, List<TerminalEntry> terminal) = prepared;
        engine.TickOneSecond();
        AircraftState aircraft = engine.FindAircraft(spawn.Callsign) ?? throw new InvalidOperationException($"{spawn.Callsign} did not spawn");
        return new Run(engine, aircraft, terminal);
    }

    private (SimulationEngine Engine, List<TerminalEntry> Terminal)? Prepare(
        Spawn spawn,
        string studentCallsign,
        string studentType,
        (string Command, int Offset)[] presets
    )
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = new SimulationEngine(new TestAirportGroundData());
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => terminal.Add(entry);
        List<string> warnings = engine.LoadScenario(ScenarioJson(spawn, presets), 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        SimScenarioState scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SoloTrainingMode = true;
        PositionConfig student = _zoa.FindPositionByCallsign(studentCallsign, facilityHint: null)!;
        scenario.StudentPosition = _zoa.ResolvePosition(student.Id);
        scenario.StudentPositionType = studentType;
        return (engine, terminal);
    }

    private static string ScenarioJson(Spawn spawn, (string Command, int Offset)[] presets)
    {
        string presetJson = string.Join(
            ", ",
            presets.Select((p, i) => $$"""{ "id": "p{{i}}", "command": "{{p.Command}}", "timeOffset": {{p.Offset}} }""")
        );
        return $$"""
            {
              "id": "runway-spawn-{{spawn.Callsign}}",
              "name": "Runway spawn call",
              "artccId": "ZOA",
              "primaryAirportId": "{{spawn.Airport}}",
              "aircraft": [
                {
                  "id": "a1",
                  "aircraftId": "{{spawn.Callsign}}",
                  "aircraftType": "{{spawn.Type}}",
                  "transponderMode": "C",
                  "spawnDelay": {{spawn.DelaySeconds}},
                  "startingConditions": { "type": "OnRunway", "runway": "{{spawn.Runway}}" },
                  "airportId": "{{spawn.Airport}}",
                  "flightplan": { "rules": "{{spawn.Rules}}", "departure": "K{{spawn.Airport}}", "destination": "KOAK", "cruiseAltitude": 9000, "cruiseSpeed": 180, "route": "", "remarks": "", "aircraftType": "{{spawn.Type}}" },
                  "presetCommands": [ {{presetJson}} ]
                }
              ]
            }
            """;
    }

    private static void Tick(Run run, int seconds) => Tick(run.Engine, seconds);

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    private static int TickUntil(Run run, Func<bool> done, int maxSeconds) => TickUntil(run.Engine, done, maxSeconds);

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
