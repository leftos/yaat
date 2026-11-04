using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// A say again is held as a flag on the aircraft, never as a queued line, so a rewind restores what is owed and a replay
/// of the recording says the same lines at the same seconds as the live run.
/// </summary>
public sealed class SayAgainReplayTests(ITestOutputHelper output)
{
    private const string Callsign = "N152SP";

    [Theory]
    [InlineData("AGAIN", false)]
    [InlineData("AGAIN CS", true)]
    public void Again_RewindWithPendingRepeat_RepeatsAfterRestore(string command, bool callsignOnly)
    {
        TestVnasData.EnsureInitialized();
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        SayAgainTests.AddAirborne(engine, "N123AB");
        Assert.True(engine.SendCommand("N123AB", "FH 270").Success);
        PilotTransmission original = Assert.Single(engine.World.DrainReadyPilotTransmissions(10, rngSeed: 1));
        Assert.True(engine.SendCommand("N123AB", command).Success);
        string json = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);

        SimulationEngine restored = SayAgainTests.NewEngine(soloTrainingMode: true);
        restored.RestoreFromSnapshot(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!);
        AircraftState aircraft = restored.FindAircraft("N123AB")!;

        Assert.Equal(!callsignOnly, aircraft.PendingRepeat);
        Assert.Equal(callsignOnly, aircraft.PendingRepeatCallsignOnly);
        PilotTransmission repeat = Assert.Single(restored.World.DrainReadyPilotTransmissions(30, rngSeed: 1));
        Assert.True(repeat.IsRepeat);
        Assert.Equal(callsignOnly ? "N123AB" : original.Text, repeat.Text);
        Assert.False(aircraft.PendingRepeat || aircraft.PendingRepeatCallsignOnly);
    }

    [Fact]
    public void Rewind_WithARepeatQueued_SpeaksItOnceAndKeepsTheLastTransmission()
    {
        TestVnasData.EnsureInitialized();
        const string Name = "N123AB";
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        AircraftState aircraft = SayAgainTests.AddAirborne(engine, Name);
        Assert.True(engine.SendCommand(Name, "FH 270").Success);
        PilotTransmission original = Assert.Single(engine.World.DrainReadyPilotTransmissions(10, rngSeed: 1));
        Assert.Equal(original.Text, aircraft.LastPilotTransmission!.Text);
        AircraftLastPilotTransmission last = aircraft.LastPilotTransmission!;

        Assert.True(engine.SendCommand(Name, "AGAIN").Success);
        // The read-back holds the frequency until t=11, so the repeat materialises behind it and waits, unaired.
        Assert.Empty(engine.World.DrainReadyPilotTransmissions(10, rngSeed: 1));
        Assert.Equal(1, engine.World.ActiveFrequency.PendingCount);
        Assert.True(engine.World.ActiveFrequency.HasPendingFrom(Name));
        Assert.True(aircraft.PendingRepeat);

        string json = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        SimulationEngine restored = SayAgainTests.NewEngine(soloTrainingMode: true);
        restored.RestoreFromSnapshot(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!);
        AircraftState restoredAircraft = restored.FindAircraft(Name)!;
        var host = SayAgainTests.RecordingHost.Around(restored.BareHost);
        for (int i = 0; i < 30; i++)
        {
            restored.RunSecond(host.AsHost);
        }

        List<PilotTransmission> repeats = [.. host.Spoken.Where(t => (t.Callsign == Name) && (t.Text == last.Text))];
        Assert.Single(repeats);
        Assert.False(restoredAircraft.PendingRepeat);
        Assert.Equal(last.AtSeconds, restoredAircraft.LastPilotTransmission!.AtSeconds);
    }

    [Fact]
    public void Unaddressed_ReplaysToTheSameTarget()
    {
        TestVnasData.EnsureInitialized();
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        AircraftState first = SayAgainTests.AddAirborne(engine, "N123AB");
        AircraftState second = SayAgainTests.AddAirborne(engine, "N456CD");
        SayAgainUnaddressedTests.QueueProactive(second, "tower, november four five six charlie delta, eight miles east, inbound landing.");
        Assert.Single(engine.World.DrainReadyPilotTransmissions(10, rngSeed: 1));
        // The awaited call and the last to transmit are different aircraft, so a restore that lost the awaited call
        // would pick the other one.
        first.LastPilotTransmission = SayAgainUnaddressedTests.LastLine(first, "fly heading 270", atSeconds: 20);
        string json = JsonSerializer.Serialize(engine.CaptureSnapshot(), RecordingJsonOptions.Default);

        Assert.True(engine.SendCommand("", "AGAINU").Success);
        PilotTransmission live = Assert.Single(engine.World.DrainReadyPilotTransmissions(70, rngSeed: 1));

        SimulationEngine restored = SayAgainTests.NewEngine(soloTrainingMode: true);
        restored.RestoreFromSnapshot(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!);
        Assert.True(restored.SendCommand("", "AGAINU").Success);
        PilotTransmission replayed = Assert.Single(restored.World.DrainReadyPilotTransmissions(70, rngSeed: 1));

        Assert.Equal("N456CD", live.Callsign);
        Assert.Equal(live.Callsign, replayed.Callsign);
        Assert.Equal(live.Text, replayed.Text);
        Assert.True(replayed.IsRepeat);
    }

    [Fact]
    public void PendingRepeatCallsignOnly_SurvivesASnapshotRoundTrip_AndIsOmittedWhenFalse()
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            PendingRepeatCallsignOnly = true,
        };

        string json = JsonSerializer.Serialize(aircraft.ToSnapshot(), RecordingJsonOptions.Default);
        var restored = AircraftState.FromSnapshot(JsonSerializer.Deserialize<AircraftSnapshotDto>(json, RecordingJsonOptions.Default)!, null);

        Assert.True(restored.PendingRepeatCallsignOnly);
        Assert.False(restored.PendingRepeat);
        aircraft.PendingRepeatCallsignOnly = false;
        Assert.DoesNotContain(
            "PendingRepeatCallsignOnly",
            JsonSerializer.Serialize(aircraft.ToSnapshot(), RecordingJsonOptions.Default),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Again_ReplayOfTheRecording_ProducesTheSameTransmissions()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            output.WriteLine("OAK ground layout unavailable — skipping");
            return;
        }

        var live = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        live.LoadScenario(ScenarioJson, rngSeed: 42, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        live.Scenario!.SoloTrainingMode = true;
        List<string> liveLines = Collect(live);

        Tick(live, 2);
        Assert.True(live.SendCommand(Callsign, "SQ 4321").Success);
        Tick(live, 10);
        Assert.True(live.SendCommand(Callsign, "AGAIN").Success);
        Tick(live, 15);
        Assert.True(live.SendCommand(Callsign, "AGAIN CS").Success);
        Tick(live, 15);

        foreach (string line in liveLines)
        {
            output.WriteLine($"[live] {line}");
        }

        // The live run repeated its last line word for word (the parked pilot's own ready-to-taxi call, which followed
        // the squawk read-back), then said the callsign alone.
        Assert.Contains(liveLines, l => l.EndsWith(": squawk 4321", StringComparison.Ordinal));
        Assert.Contains(liveLines.Select(TextOf).GroupBy(t => t), g => g.Count() == 2);
        Assert.Contains(liveLines, l => l.EndsWith($" {Callsign}: {Callsign}", StringComparison.Ordinal));

        var recording = new SessionRecording
        {
            ScenarioJson = ScenarioJson,
            RngSeed = 42,
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            Actions = [.. live.Scenario.ActionLog],
            TotalElapsedSeconds = live.Scenario.ElapsedSeconds,
        };
        var replay = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        List<string> replayLines = Collect(replay);
        replay.ReplayWithScenarioOverride(recording, live.Scenario.ElapsedSeconds, scenario => scenario.SoloTrainingMode = true);

        Assert.Equal(liveLines, replayLines);
    }

    /// <summary>Every pilot line for <see cref="Callsign"/> the terminal shows, as "second kind callsign: text".</summary>
    private static List<string> Collect(SimulationEngine engine)
    {
        var lines = new List<string>();
        engine.TerminalEntryEmitted += entry =>
        {
            if ((entry.Callsign == Callsign) && (entry.Kind is "SayPilot" or "SayReadback"))
            {
                lines.Add($"{engine.Scenario?.ElapsedSeconds:F0} {entry.Kind} {entry.Callsign}: {entry.Message}");
            }
        };
        return lines;
    }

    /// <summary>A collected line without its second: "kind callsign: text".</summary>
    private static string TextOf(string line) => line[(line.IndexOf(' ') + 1)..];

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    private const string ScenarioJson = """
        {
          "id": "say-again-replay",
          "name": "Say again replay",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "N152SP",
              "aircraftType": "C172",
              "transponderMode": "C",
              "startingConditions": { "type": "Parking", "parking": "SIG1" },
              "flightplan": {
                "rules": "VFR",
                "departure": "KOAK",
                "destination": "KSQL",
                "cruiseAltitude": 3500,
                "cruiseSpeed": 100,
                "route": "",
                "remarks": "",
                "aircraftType": "C172"
              },
              "presetCommands": []
            }
          ]
        }
        """;
}
