using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The delayed initial calls (YAAT-308): a spawn whose only ground preset is a push calls "ready to taxi" a setup delay
/// after the push (jet 90 s, piston 30 s), and a spawn whose preset taxi ends at a spot or a taxiway hold short calls
/// 10 to 20 s after it comes to rest there — each after a pacing slot, with the preset strings real ZOA scenarios carry.
/// </summary>
public class DelayedInitialCallupE2ETests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public DelayedInitialCallupE2ETests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void JetPushPreset_CallsPushedBackFromTheGate_NinetySecondsAfterThePush()
    {
        if (Load("SFO", "UAL926", "B738", "G9", ("PUSH T9", 0)) is not { } run)
        {
            return;
        }

        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "UAL926") is HoldingAfterPushbackPhase, 300) >= 0, "the push should complete");
        Tick(run.Engine, 88);
        Assert.Empty(ReadyToTaxiLines(run.Said, "UAL926"));

        int extra = TickUntil(run.Engine, () => ReadyToTaxiLines(run.Said, "UAL926").Count > 0, 10);

        Assert.InRange(extra, 1, 3);
        TerminalEntry line = Assert.Single(ReadyToTaxiLines(run.Said, "UAL926"));
        Assert.Contains(", pushed back from gate G9, with information ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(", IFR to Los Angeles Airport, ready to taxi.", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PistonPushPreset_CallsThirtySecondsAfterThePush()
    {
        if (Load("OAK", "N152SP", "C172", "SIG1", ("PUSH", 0)) is not { } run)
        {
            return;
        }

        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "N152SP") is HoldingAfterPushbackPhase, 300) >= 0, "the push should complete");
        Tick(run.Engine, 28);
        Assert.Empty(ReadyToTaxiLines(run.Said, "N152SP"));

        Assert.InRange(TickUntil(run.Engine, () => ReadyToTaxiLines(run.Said, "N152SP").Count > 0, 10), 1, 3);
        Assert.Contains(", pushed back from parking SIG1, ", Assert.Single(ReadyToTaxiLines(run.Said, "N152SP")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PushPreset_WaitsForAPacingSlot()
    {
        if (Load("OAK", "N152SP", "C172", "SIG1", ("PUSH", 0)) is not { } run)
        {
            return;
        }

        run.Engine.Scenario!.SoloParkingInitialCallupRatePercent = 0;

        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "N152SP") is HoldingAfterPushbackPhase, 300) >= 0, "the push should complete");
        Tick(run.Engine, 90);

        Assert.Empty(ReadyToTaxiLines(run.Said, "N152SP"));
        Assert.False(run.Engine.FindAircraft("N152SP")!.Ground.InitialCallupDecisionProcessed);
    }

    [Fact]
    public void PushToASpot_CallsFromTheSpot()
    {
        if (Load("SFO", "UAL790", "B738", "C9", ("PUSH $5A", 0)) is not { } run)
        {
            return;
        }

        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "UAL790") is HoldingAfterPushbackPhase, 300) >= 0, "the push should complete");
        Assert.Equal("5A", run.Engine.FindAircraft("UAL790")!.Ground.PushEndSpot);
        Tick(run.Engine, 95);

        Assert.Contains(", at spot 5A, with information ", Assert.Single(ReadyToTaxiLines(run.Said, "UAL790")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PushToAnotherStand_CallsFromThatStandAfterTheDelay()
    {
        if (Load("SFO", "SWA1360", "B737", "B12", ("PUSH @B13", 0)) is not { } run)
        {
            return;
        }

        bool parked =
            TickUntil(
                run.Engine,
                () => (PhaseOf(run, "SWA1360") is AtParkingPhase) && (run.Engine.FindAircraft("SWA1360")!.Ground.ParkingSpot == "B13"),
                300
            ) >= 0;
        Assert.True(parked, "the push should park the aircraft on B13");
        Tick(run.Engine, 88);
        Assert.Empty(ReadyToTaxiLines(run.Said, "SWA1360"));

        Assert.InRange(TickUntil(run.Engine, () => ReadyToTaxiLines(run.Said, "SWA1360").Count > 0, 10), 1, 3);
        Assert.Contains(", at gate B13, with information ", Assert.Single(ReadyToTaxiLines(run.Said, "SWA1360")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterPushAircraft_NeverCallsBeforeItsPush()
    {
        if (Load("OAK", "N152SP", "C172", "SIG1", ("PUSH", 120)) is not { } run)
        {
            return;
        }

        Tick(run.Engine, 115);

        AircraftState ac = run.Engine.FindAircraft("N152SP")!;
        Assert.IsType<AtParkingPhase>(ac.Phases!.CurrentPhase);
        Assert.Equal(InitialCallupPlan.AfterPush, ac.Ground.InitialCallup);
        Assert.Null(ac.Ground.PushedBackFrom);
        Assert.Empty(ReadyToTaxiLines(run.Said, "N152SP"));
    }

    [Fact]
    public void PresetTaxiToSpot_CallsAtTheSpotTenToTwentySecondsAfterStopping()
    {
        if (Load("SFO", "AFR83", "B77W", "A8", ("PUSH M4", 0), ("WAIT 30 TAXI M4 M1 $1", 0)) is not { } run)
        {
            return;
        }

        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "AFR83") is HoldingInPositionPhase, 1200) >= 0, "the preset taxi should reach spot 1");
        Assert.Empty(ReadyToTaxiLines(run.Said, "AFR83"));

        int waited = TickUntil(run.Engine, () => ReadyToTaxiLines(run.Said, "AFR83").Count > 0, 30);

        double delay = InitialCallupCall.AfterTaxiArrivalDelaySeconds("AFR83");
        Assert.Equal(13.0, delay);
        Assert.InRange(waited, (int)Math.Floor(delay), (int)Math.Ceiling(delay) + 2);
        Assert.Contains(", at spot 1, with information ", Assert.Single(ReadyToTaxiLines(run.Said, "AFR83")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ControllerTaxiElsewhereBeforeArrival_GivesNoCall()
    {
        if (Load("SFO", "AFR83", "B77W", "A8", ("PUSH M4", 0), ("WAIT 30 TAXI M4 M1 $1", 0)) is not { } run)
        {
            return;
        }

        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "AFR83") is TaxiingPhase, 600) >= 0, "the preset taxi should start");
        CommandResult redirect = run.Engine.SendCommand("AFR83", "TAXI M4 M1");
        Assert.True(redirect.Success, redirect.Message);

        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "AFR83") is HoldingInPositionPhase, 1200) >= 0, "the redirected taxi should end");
        Tick(run.Engine, 30);

        Assert.Empty(ReadyToTaxiLines(run.Said, "AFR83"));
    }

    [Fact]
    public void PresetTaxiToTaxiwayHoldShort_CallsOnceWithTheHoldShortLocative()
    {
        if (Load("SFO", "N43778", "C25C", "41-4", ("TAXI T41W C HS C", 0)) is not { } run)
        {
            return;
        }

        var warnings = new List<string>();
        run.Engine.WarningEmitted += (_, warning) => warnings.Add(warning);
        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "N43778") is HoldingShortPhase, 600) >= 0, "the preset taxi should reach the bar");
        Tick(run.Engine, 25);

        // Ground answers the call, so the call replaces the hold-short report rather than following it.
        Assert.DoesNotContain(warnings, w => w.Contains("holding short of C at T41W", StringComparison.OrdinalIgnoreCase));
        List<TerminalEntry> holdingShort =
        [
            .. run.Said.Where(e => (e.Callsign == "N43778") && e.Message.Contains("holding short of C", StringComparison.OrdinalIgnoreCase)),
        ];
        TerminalEntry line = Assert.Single(holdingShort);
        Assert.Contains(", holding short of C at T41W, with information ", line.Message, StringComparison.Ordinal);
        Assert.EndsWith(", ready to taxi.", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PresetTaxiCall_ComesAtTheSameSimTimeAcrossRuns()
    {
        if ((CallTimeOfHoldShortPreset() is not { } first) || (CallTimeOfHoldShortPreset() is not { } second))
        {
            return;
        }

        Assert.True(first > 0, "the call should come");
        Assert.Equal(first, second);
    }

    [Fact]
    public void AiGround_AnswersTheCallAtATaxiwayBar_AndTheRequestStaysOpenThere()
    {
        if (Load("SFO", "N43778", "C25C", "41-4", ("TAXI T41W C HS C", 0)) is not { } run)
        {
            return;
        }

        Assert.True(TickUntil(run.Engine, () => ReadyToTaxiLines(run.Said, "N43778").Count > 0, 600) >= 0, "the call should come");
        Tick(run.Engine, 5);

        AircraftState ac = run.Engine.FindAircraft("N43778")!;
        Assert.IsType<HoldingShortPhase>(ac.Phases!.CurrentPhase);
        Assert.True(ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi }, "the request stays open at the bar");
        Assert.True(AnswerTaxiOutRule.Applies(ac));
    }

    [Fact]
    public void AiGround_AnswersTheCallAtASpot()
    {
        if (Load("SFO", "AFR83", "B77W", "A8", ("PUSH M4", 0), ("WAIT 30 TAXI M4 M1 $1", 0)) is not { } run)
        {
            return;
        }

        Assert.True(TickUntil(run.Engine, () => ReadyToTaxiLines(run.Said, "AFR83").Count > 0, 1300) >= 0, "the call should come");
        Tick(run.Engine, 5);

        AircraftState ac = run.Engine.FindAircraft("AFR83")!;
        Assert.True(ac.PendingPilotRequest is { IsOpen: true, Kind: PilotPendingRequestKind.Taxi }, "the request stays open at the spot");
        Assert.True(AnswerTaxiOutRule.Applies(ac));
    }

    [Fact]
    public void AiGround_LeavesAnAircraftHoldingShortWithoutATaxiRequestAlone()
    {
        if (Load("SFO", "N43778", "C25C", "41-4", ("TAXI T41W C HS C", 0)) is not { } run)
        {
            return;
        }

        run.Engine.Scenario!.SoloParkingInitialCallupRatePercent = 0;
        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "N43778") is HoldingShortPhase, 600) >= 0, "the preset taxi should reach the bar");
        Tick(run.Engine, 30);

        AircraftState ac = run.Engine.FindAircraft("N43778")!;
        Assert.False(ac.PendingPilotRequest is { IsOpen: true });
        Assert.False(AnswerTaxiOutRule.Applies(ac));
    }

    [Fact]
    public void PacingRateZero_TheHoldShortReportStillFires()
    {
        if (Load("SFO", "N43778", "C25C", "41-4", ("TAXI T41W C HS C", 0)) is not { } run)
        {
            return;
        }

        var warnings = new List<string>();
        run.Engine.WarningEmitted += (_, warning) => warnings.Add(warning);
        run.Engine.Scenario!.SoloParkingInitialCallupRatePercent = 0;
        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "N43778") is HoldingShortPhase, 600) >= 0, "the preset taxi should reach the bar");
        Tick(run.Engine, 30);

        Assert.Contains(warnings, w => w.Contains("holding short of C at T41W", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(ReadyToTaxiLines(run.Said, "N43778"));
    }

    [Fact]
    public void SnapshotDuringThePostPushDelay_RestoresAndCallsAtTheSameSimTime()
    {
        if (Load("OAK", "N152SP", "C172", "SIG1", ("PUSH", 0)) is not { } run)
        {
            return;
        }

        Assert.True(TickUntil(run.Engine, () => PhaseOf(run, "N152SP") is HoldingAfterPushbackPhase, 300) >= 0, "the push should complete");
        Tick(run.Engine, 10);
        string json = JsonSerializer.Serialize(run.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        int straight = TickUntil(run.Engine, () => ReadyToTaxiLines(run.Said, "N152SP").Count > 0, 60);

        Run restored = Load("OAK", "N152SP", "C172", "SIG1", ("PUSH", 0))!;
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;
        restored.Engine.RestoreFromSnapshot(snapshot);
        int afterRestore = TickUntil(restored.Engine, () => ReadyToTaxiLines(restored.Said, "N152SP").Count > 0, 60);

        Assert.InRange(straight, 18, 23);
        Assert.Equal(straight, afterRestore);
    }

    private int? CallTimeOfHoldShortPreset()
    {
        if (Load("SFO", "N43778", "C25C", "41-4", ("TAXI T41W C HS C", 0)) is not { } run)
        {
            return null;
        }

        return TickUntil(run.Engine, () => ReadyToTaxiLines(run.Said, "N43778").Count > 0, 600);
    }

    private sealed record Run(SimulationEngine Engine, List<TerminalEntry> Said);

    private Run? Load(string airport, string callsign, string type, string parking, params (string Command, int Offset)[] presets)
    {
        var groundData = new TestAirportGroundData();
        if ((_zoa is null) || (groundData.GetLayout(airport) is null))
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        List<string> warnings = engine.LoadScenario(
            ScenarioJson(airport, callsign, type, parking, presets),
            42,
            MagneticDeclination.EvaluationDateUtc
        );
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        SimScenarioState scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SoloTrainingMode = true;
        scenario.SetAiStaffedPositions([airport == "SFO" ? TestAiPositions.SfoGround(_zoa) : TestAiPositions.OakGround(_zoa)]);
        var said = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => said.Add(entry);
        return new Run(engine, said);
    }

    private static string ScenarioJson(string airport, string callsign, string type, string parking, (string Command, int Offset)[] presets)
    {
        string presetJson = string.Join(
            ", ",
            presets.Select((p, i) => $$"""{ "id": "p{{i}}", "command": "{{p.Command}}", "timeOffset": {{p.Offset}} }""")
        );
        return $$"""
            {
              "id": "delayed-callup-{{callsign}}",
              "name": "Delayed initial call-up",
              "artccId": "ZOA",
              "primaryAirportId": "{{airport}}",
              "aircraft": [
                {
                  "id": "a1",
                  "aircraftId": "{{callsign}}",
                  "aircraftType": "{{type}}",
                  "transponderMode": "C",
                  "startingConditions": { "type": "Parking", "parking": "{{parking}}" },
                  "flightplan": { "rules": "IFR", "departure": "K{{airport}}", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450, "route": "", "remarks": "", "aircraftType": "{{type}}" },
                  "presetCommands": [ {{presetJson}} ]
                }
              ]
            }
            """;
    }

    private static Phase? PhaseOf(Run run, string callsign) => run.Engine.FindAircraft(callsign)?.Phases?.CurrentPhase;

    /// <summary>Ticks one second at a time until <paramref name="done"/> holds; the ticks it took, or -1 when it never held.</summary>
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

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    private static List<TerminalEntry> ReadyToTaxiLines(List<TerminalEntry> said, string callsign) =>
        [
            .. said.Where(e =>
                string.Equals(e.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains("ready to taxi", StringComparison.OrdinalIgnoreCase)
            ),
        ];
}
