using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A scenario preset led by <c>AS {tcp}</c>, bare or behind its WAIT/WAITD gate: <see cref="PresetActingAs.Split"/> hoists
/// the AS off the preset text, and the engine dispatches the rest acting as that position. The engine rows run over the
/// real ZOA data: the list <c>POAK</c> ("OAK DEP"), whose only sender is NCT's TCP 3O (the student position OAK_TWR) and
/// whose receivers include 4U.
/// </summary>
public class PresetActingAsTests
{
    private const string ListId = "POAK";
    private const string CallsignA = "SWA1234";

    /// <summary>ZOA's OAK_TWR ("LC1"), whose STARS TCP is 3O — <c>POAK</c>'s only sender.</summary>
    private const string OakTwrPositionId = "01GEAMB98RKCPP9HCNPW5AVDA5";

    private const string CouldNotApply = "[Preset] could not apply";

    private const string DeferredCouldNotApply = "[Deferred] could not apply";

    /// <summary>Two IFR departures parked on the OAK field.</summary>
    private const string TwoAtOak = """
        {
          "id": "coordination-two",
          "name": "Two departures at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "SWA1234",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.7213, "lon": -122.2208 },
                "altitude": 9,
                "heading": 290,
                "speed": 0
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KOAK",
                "destination": "KSFO",
                "cruiseAltitude": 35000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            },
            {
              "id": "a2",
              "aircraftId": "SWA5678",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.7205, "lon": -122.2190 },
                "altitude": 9,
                "heading": 290,
                "speed": 0
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KOAK",
                "destination": "KSAN",
                "cruiseAltitude": 33000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ]
        }
        """;

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public PresetActingAsTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("AS 1Z RD PMRY", "1Z", "RD PMRY")]
    [InlineData("WAIT 5 AS 1Z RD PMRY", "1Z", "WAIT 5 RD PMRY")]
    [InlineData("WAITD 3 AS 1Z RD PMRY", "1Z", "WAITD 3 RD PMRY")]
    [InlineData("as 1z rd pmry", "1z", "rd pmry")]
    [InlineData("RD PMRY", null, "RD PMRY")]
    [InlineData("CM 50", null, "CM 50")]
    [InlineData("AS 1Z", null, "AS 1Z")]
    [InlineData("WAIT 5 RD PMRY", null, "WAIT 5 RD PMRY")]
    [InlineData("WAIT AS 1Z RD PMRY", null, "WAIT AS 1Z RD PMRY")]
    [InlineData("FH 270 AS 1Z RD PMRY", null, "FH 270 AS 1Z RD PMRY")]
    public void Split_HoistsALeadingAs(string preset, string? expectedTcp, string expectedCommand)
    {
        (string? tcp, string command) = PresetActingAs.Split(preset);

        Assert.Equal(expectedTcp, tcp);
        Assert.Equal(expectedCommand, command);
    }

    [Fact]
    public void ImmediatePreset_AsSenderTcp_Rd_ReleasesOnTheList()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, CallsignA, "AS 3O RD POAK");

        CoordinationItem item = Assert.Single(Channel(engine).Items);
        Assert.Equal(CallsignA, item.AircraftId);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, item.Status);
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void ImmediatePreset_AsReceiverTcp_Rdack_Acknowledges()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, CallsignA, "AS 3O RD POAK");
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, Assert.Single(Channel(engine).Items).Status);

        DispatchPresets(engine, CallsignA, "AS 4U RDACK POAK");

        Assert.Equal(StarsCoordinationStatus.Acknowledged, Assert.Single(Channel(engine).Items).Status);
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void ImmediatePreset_AsUnknownTcp_RefusesWithUnknownPosition()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, CallsignA, "AS 2A RD POAK");

        Assert.Contains(entries, e => (e.Kind == "Warning") && (e.Message == "[Preset] could not apply: Unknown position: 2A"));
        Assert.Empty(Channel(engine).Items);
    }

    [Fact]
    public void ImmediatePreset_RdWithoutAs_RefusesNoActivePosition()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, CallsignA, "RD POAK");

        Assert.Contains(
            entries,
            e => (e.Kind == "Warning") && e.Message.Contains("No active position — use AS to set one", StringComparison.Ordinal)
        );
        Assert.Empty(Channel(engine).Items);
    }

    [Fact]
    public void ImmediatePreset_AsTcp_AviationCommand_DispatchesAsScripted()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = AddAirborne(engine, "SWA9001", altitude: 3000);
        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, ac.Callsign, "AS 3O CM 50");

        Assert.Equal(5000, ac.Targets.AssignedAltitude);
        Assert.DoesNotContain(entries, e => e.Kind == "Warning");
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void ImmediatePreset_AsTcp_NotComposedWithCfix()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = AddAirborne(engine, "SWA9002", altitude: 8000);
        CommandResult dct = engine.SendCommand(ac.Callsign, "DCT SUNOL");
        Assert.True(dct.Success, dct.Message);

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, ac.Callsign, "CFIX SUNOL 060", "AS 3O RD POAK");

        CoordinationItem item = Assert.Single(Channel(engine).Items);
        Assert.Equal(ac.Callsign, item.AircraftId);
        Assert.Contains(ac.Targets.NavigationRoute, f => (f.Name == "SUNOL") && (f.AltitudeRestriction is not null));
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void ImmediatePreset_AsReceiverTcp_Rdauto_TogglesAutoAck()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        bool before = Channel(engine).Receivers.Any(r => r.AutoAcknowledge);
        DispatchPresets(engine, CallsignA, "AS 4U RDAUTO POAK");

        Assert.NotEqual(before, Channel(engine).Receivers.Any(r => r.AutoAcknowledge));
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(CouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void WaitPreset_AsSenderTcp_Rd_ReleasesWhenTheWaitEnds()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, CallsignA, "WAIT 5 AS 3O RD POAK");
        Tick(engine, 4);
        Assert.Empty(Channel(engine).Items);

        Tick(engine, 2);

        CoordinationItem item = Assert.Single(Channel(engine).Items);
        Assert.Equal(CallsignA, item.AircraftId);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, item.Status);
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(DeferredCouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void WaitdPreset_AsSenderTcp_Rd_ReleasesAfterTheDistance()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = AddAirborne(engine, "SWA9003", altitude: 8000);
        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, ac.Callsign, "WAITD 1 AS 3O RD POAK");

        double flownNm = 0;
        int seconds = 0;
        while (flownNm < 0.9)
        {
            Assert.True(++seconds < 120, $"test setup: {ac.Callsign} should fly 1 nm inside two minutes");
            Assert.Empty(Channel(engine).Items);
            Tick(engine, 1);
            flownNm += ac.GroundSpeed / 3600.0;
        }

        while (flownNm < 1.1)
        {
            Tick(engine, 1);
            flownNm += ac.GroundSpeed / 3600.0;
        }

        CoordinationItem item = Assert.Single(Channel(engine).Items);
        Assert.Equal(ac.Callsign, item.AircraftId);
        Assert.DoesNotContain(entries, e => e.Message.StartsWith(DeferredCouldNotApply, StringComparison.Ordinal));
    }

    [Fact]
    public void WaitPreset_RdWithoutAs_RefusesAtFire()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<TerminalEntry> entries = Capture(engine);
        DispatchPresets(engine, CallsignA, "WAIT 5 RD POAK");
        Tick(engine, 6);

        Assert.Contains(entries, e => e.Message == $"{DeferredCouldNotApply}: No active position — use AS to set one");
        Assert.Empty(Channel(engine).Items);
    }

    [Fact]
    public void WaitPreset_AsTcp_SurvivesASnapshotRestore()
    {
        if (Engine() is not { } engine || Engine() is not { } fresh)
        {
            return;
        }

        DispatchPresets(engine, CallsignA, "WAIT 10 AS 3O RD POAK");
        Tick(engine, 3);
        Assert.Empty(Channel(engine).Items);

        fresh.RestoreFromSnapshot(engine.CaptureSnapshot());
        Tick(fresh, 8);

        CoordinationItem item = Assert.Single(Channel(fresh).Items);
        Assert.Equal(CallsignA, item.AircraftId);
    }

    [Fact]
    public void Replay_T0PresetWithAs_ResolvesAsTheLiveRoomDid()
    {
        if (_zoa is null)
        {
            return;
        }

        const string Empty = "\"presetCommands\": [],";
        int first = TwoAtOak.IndexOf(Empty, StringComparison.Ordinal);
        string json = string.Concat(
            TwoAtOak.AsSpan(0, first),
            "\"presetCommands\": [ { \"id\": \"p0\", \"command\": \"AS 3O RD POAK\", \"timeOffset\": 0 } ],",
            TwoAtOak.AsSpan(first + Empty.Length)
        );
        var recording = new SessionRecording
        {
            ScenarioJson = json,
            RngSeed = 7,
            Actions = [],
            TotalElapsedSeconds = 0,
            ArtccConfigJson = JsonSerializer.Serialize(_zoa, RecordingJsonOptions.Default),
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
            StudentPositionState = new ReplayStudentPosition(
                _zoa.ResolvePosition(OakTwrPositionId),
                _zoa.GetTcpForPosition(OakTwrPositionId),
                null,
                true
            ),
        };

        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.Replay(recording, 0);

        CoordinationItem item = Assert.Single(Channel(engine).Items);
        Assert.Equal(CallsignA, item.AircraftId);
        Assert.Equal(StarsCoordinationStatus.Unacknowledged, item.Status);
    }

    /// <summary>
    /// A bare engine with the ZOA configuration resolved and the student position set the way the server's load
    /// resolves it, then the ARTCC initialisation that load runs — which is what puts <c>POAK</c> on the engine.
    /// </summary>
    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(TwoAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = _zoa.ResolvePosition(OakTwrPositionId);
        scenario.StudentTcp = _zoa.GetTcpForPosition(OakTwrPositionId);
        engine.InitializeFromArtcc();
        Assert.True(scenario.CoordinationChannels.ContainsKey(ListId), "POAK should carry a coordination channel from the ZOA tree");
        return engine;
    }

    private static CoordinationChannel Channel(SimulationEngine engine) => engine.Scenario!.CoordinationChannels[ListId];

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    private static List<TerminalEntry> Capture(SimulationEngine engine)
    {
        var entries = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entries.Add;
        return entries;
    }

    private static void DispatchPresets(SimulationEngine engine, string callsign, params string[] commands)
    {
        AircraftState ac = engine.FindAircraft(callsign)!;
        var loaded = new LoadedAircraft { State = ac, PresetCommands = [.. commands.Select(c => new PresetCommand { Command = c, TimeOffset = 0 })] };
        engine.DispatchPresetCommands(loaded);
    }

    /// <summary>An airborne A319 east of OAK, clear of the field.</summary>
    private static AircraftState AddAirborne(SimulationEngine engine, string callsign, double altitude)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "A319",
            Position = new LatLon(37.75, -122.0),
            TrueHeading = new TrueHeading(90),
            Altitude = altitude,
            IndicatedAirspeed = 250,
            FlightPlan = new AircraftFlightPlan(),
        };
        engine.World.AddAircraft(ac);
        return ac;
    }
}
