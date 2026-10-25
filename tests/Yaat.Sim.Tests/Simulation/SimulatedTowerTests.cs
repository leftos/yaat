using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The silent simulated tower (YAAT-1180) for departures: where the student does not work the tower and no AI tower covers
/// the airport, a departure the student transferred (CT or FCA on the ground) is cleared for takeoff at its runway a short
/// delay after the transfer, once the runway is clear; one that was not transferred holds. SWA1234 (B738) taxis from OAK
/// stand SIG4 to the bar of 28R under a ZOA student on OAK ground.
/// </summary>
[Collection("NavDbMutator")]
public class SimulatedTowerTests
{
    private const string Callsign = "SWA1234";
    private const string SimTowerNote = "[Sim tower]";

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public SimulatedTowerTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Transferred_AtTheBar_IsClearedAfterItsDelay()
    {
        if (LoadAtTheBar("OAK_GND", "GND", AtcPositionLevel.Ground) is not { } run)
        {
            return;
        }

        SimScenarioState scenario = run.Engine.Scenario!;
        double stamp = Transfer(run);
        int delay = SimulationEngine.SimulatedTowerCtoDelaySeconds(Callsign);
        Assert.InRange(delay, 5, 20);
        Assert.Equal(scenario.ElapsedSeconds, stamp);

        while (scenario.ElapsedSeconds < stamp + delay - 1)
        {
            run.Engine.TickOneSecond();
            Assert.False(SimTowerCleared(run), $"cleared at {scenario.ElapsedSeconds}, before stamp {stamp} + delay {delay}");
        }

        Assert.Equal(stamp + delay - 1, scenario.ElapsedSeconds);
        run.Engine.TickOneSecond();
        Assert.Equal(stamp + delay, scenario.ElapsedSeconds);
        Assert.True(SimTowerCleared(run), "not cleared at stamp + delay");
        Assert.Contains(run.Terminal, t => t.Message == "[Sim tower] Cleared for takeoff, runway 28R");
    }

    [Fact]
    public void StudentCtoBetweenTicks_IsNotClearedTwice()
    {
        if (LoadAtTheBar("OAK_GND", "GND", AtcPositionLevel.Ground) is not { } run)
        {
            return;
        }

        SimScenarioState scenario = run.Engine.Scenario!;
        double stamp = Transfer(run);
        int delay = SimulationEngine.SimulatedTowerCtoDelaySeconds(Callsign);
        while (scenario.ElapsedSeconds < stamp + delay - 1)
        {
            run.Engine.TickOneSecond();
        }

        CommandResult cto = run.Engine.SendCommand(Callsign, "CTO");
        Assert.True(cto.Success, cto.Message);
        run.Engine.TickOneSecond();

        Assert.False(SimTowerCleared(run), "the simulated tower cleared a departure the student had just cleared");
        Assert.Single(run.Aircraft.Phases!.Phases, p => p is TakeoffPhase);
    }

    [Fact]
    public void NotTransferred_Holds()
    {
        if (LoadAtTheBar("OAK_GND", "GND", AtcPositionLevel.Ground) is not { } run)
        {
            return;
        }

        Tick(run.Engine, 120);

        Assert.False(SimTowerCleared(run));
        Assert.IsType<HoldingShortPhase>(run.Aircraft.Phases!.CurrentPhase);
        Assert.Null(run.Aircraft.Phases.DepartureClearance);
    }

    [Fact]
    public void TransferredWhileTaxiing_IsClearedOnArrival()
    {
        if (Load("OAK_GND", "GND", AtcPositionLevel.Ground, withRunwayBlocker: false) is not { } run)
        {
            return;
        }

        CommandResult taxi = run.Engine.SendCommand(Callsign, "TAXIAUTO 28R");
        Assert.True(taxi.Success, taxi.Message);
        double stamp = Transfer(run);
        int delay = SimulationEngine.SimulatedTowerCtoDelaySeconds(Callsign);

        Assert.True(TickUntil(run.Engine, () => IsAtTheBar(run.Aircraft), 900) > 0, "never reached the bar of its departure runway");
        Assert.True(run.Engine.Scenario!.ElapsedSeconds >= stamp + delay, "the taxi was shorter than the delay");
        Assert.False(SimTowerCleared(run), "cleared before it reached the bar");

        Assert.Equal(1, TickUntil(run.Engine, () => SimTowerCleared(run), 5));
    }

    [Fact]
    public void AiTower_NothingHappens()
    {
        if (LoadAtTheBar("OAK_GND", "GND", AtcPositionLevel.Ground, zoa => [TestAiPositions.OakTower(zoa)]) is not { } run)
        {
            return;
        }

        Transfer(run);
        Tick(run.Engine, 60);

        Assert.False(SimTowerCleared(run), "the simulated tower cleared a departure an AI tower works");
    }

    [Fact]
    public void TowerStudent_NothingHappens()
    {
        if (LoadAtTheBar("OAK_TWR", "TWR", AtcPositionLevel.Ground) is not { } run)
        {
            return;
        }

        Transfer(run);
        Tick(run.Engine, 60);

        Assert.False(SimTowerCleared(run));
        Assert.IsType<HoldingShortPhase>(run.Aircraft.Phases!.CurrentPhase);
    }

    [Fact]
    public void NullRange_NothingHappens()
    {
        if (LoadAtTheBar("OAK_GND", "GND", null) is not { } run)
        {
            return;
        }

        Transfer(run);
        Tick(run.Engine, 60);

        Assert.False(SimTowerCleared(run));
        Assert.IsType<HoldingShortPhase>(run.Aircraft.Phases!.CurrentPhase);
    }

    [Fact]
    public void HeldForRelease_IsLeftToTheHfrPath()
    {
        if (Load("OAK_GND", "GND", AtcPositionLevel.Ground, withRunwayBlocker: false) is not { } run)
        {
            return;
        }

        SimScenarioState scenario = run.Engine.Scenario!;
        Assert.True(HeldReleaseService.Arm(scenario, run.Engine.World, "KOAK").Success);
        Assert.True(run.Aircraft.Ground.HeldForRelease);
        TaxiToTheBar(run);
        Transfer(run);
        Tick(run.Engine, 60);

        Assert.False(SimTowerCleared(run), "the simulated tower cleared a departure held for release");
        Assert.IsType<HoldingShortPhase>(run.Aircraft.Phases!.CurrentPhase);

        Assert.True(HeldReleaseService.Release(scenario, run.Engine.World, run.Engine.World.Rng, Callsign, null).Success);
        Assert.True(
            TickUntil(run.Engine, () => run.Aircraft.Phases?.CurrentPhase is not HoldingShortPhase, 120) > 0,
            "never departed on its release"
        );

        Assert.False(SimTowerCleared(run), "the simulated tower cleared a departure the hold-for-release path had just cleared");
        Assert.Contains(run.Terminal, t => t.Message.Contains("[HFR] Released", StringComparison.Ordinal));
    }

    [Fact]
    public void RunwayOccupied_Waits()
    {
        if (Load("OAK_GND", "GND", AtcPositionLevel.Ground, withRunwayBlocker: true) is not { } run)
        {
            return;
        }

        AircraftState blocker = run.Engine.FindAircraft("AAL100") ?? throw new InvalidOperationException("AAL100 did not spawn");
        TaxiToTheBar(run);
        Transfer(run);
        Tick(run.Engine, 40);

        Assert.False(SimTowerCleared(run), "cleared with an aircraft lined up on the runway");
        Assert.True(blocker.IsOnGround);

        CommandResult cto = run.Engine.SendCommand("AAL100", "CTO");
        Assert.True(cto.Success, cto.Message);
        Assert.True(TickUntil(run.Engine, () => SimTowerCleared(run), 240) > 0, "never cleared once the runway was clear");
        Assert.False(blocker.IsOnGround, "cleared before the departure ahead had left the runway");
    }

    [Fact]
    public void ArrivalInsideSixtySeconds_Waits()
    {
        RunwayInfo? runway = NavigationDatabase.Instance.GetRunway("OAK", "28R");
        if (runway is null)
        {
            return;
        }

        var departure = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            AirportId = "OAK",
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            IsOnGround = true,
        };

        AircraftState near = OnFinal(runway, "N152SP", 2.0);
        Assert.False(SimulatedTowerRunwayGuard.IsClear(departure, runway, [departure, near], null, out string? reason));
        Assert.Contains("N152SP", reason, StringComparison.Ordinal);

        AircraftState far = OnFinal(runway, "N152SP", 6.0);
        Assert.True(SimulatedTowerRunwayGuard.IsClear(departure, runway, [departure, far], null, out string? clearReason));
        Assert.Null(clearReason);
    }

    [Fact]
    public void SnapshotRestoreMidDelay_ClearsAtTheSameSecond()
    {
        if (LoadAtTheBar("OAK_GND", "GND", AtcPositionLevel.Ground) is not { } straight)
        {
            return;
        }

        Transfer(straight);
        Assert.True(TickUntil(straight.Engine, () => IsCleared(straight.Engine), 60) > 0, "never cleared");
        double straightClearedAt = straight.Engine.Scenario!.ElapsedSeconds;

        Run restored = LoadAtTheBar("OAK_GND", "GND", AtcPositionLevel.Ground) ?? throw new InvalidOperationException("second load failed");
        double stamp = Transfer(restored);
        Tick(restored.Engine, 2);
        Assert.False(IsCleared(restored.Engine), "cleared before the snapshot");

        string json = JsonSerializer.Serialize(restored.Engine.CaptureSnapshot(), RecordingJsonOptions.Default);
        restored.Engine.RestoreFromSnapshot(JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!);
        AircraftState aircraft = restored.Engine.FindAircraft(Callsign) ?? throw new InvalidOperationException($"{Callsign} lost in the restore");
        Assert.Equal(stamp, aircraft.Ground.LeftStudentFrequencyAtSeconds);

        Assert.True(TickUntil(restored.Engine, () => IsCleared(restored.Engine), 60) > 0, "never cleared after the restore");
        Assert.Equal(straightClearedAt, restored.Engine.Scenario!.ElapsedSeconds);
        Assert.Equal(stamp + SimulationEngine.SimulatedTowerCtoDelaySeconds(Callsign), straightClearedAt);
    }

    [Fact]
    public void SimulatedTowerCtoDelaySeconds_StaysInRange()
    {
        foreach (string callsign in new[] { "SWA1234", "AAL1828", "N152SP", "UAL1", "SKW5432", "N513SJ", "DAL2011", "A" })
        {
            int delay = SimulationEngine.SimulatedTowerCtoDelaySeconds(callsign);
            Assert.InRange(delay, 5, 20);
            Assert.Equal(delay, SimulationEngine.SimulatedTowerCtoDelaySeconds(callsign));
        }
    }

    private static AircraftState OnFinal(RunwayInfo runway, string callsign, double distanceNm)
    {
        var threshold = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        return new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            AirportId = "OAK",
            Position = GeoMath.ProjectPoint(threshold, runway.TrueHeading.ToReciprocal(), distanceNm),
            TrueHeading = runway.TrueHeading,
            TrueTrack = runway.TrueHeading,
            Altitude = runway.ElevationFt + (distanceNm * 318),
            IndicatedAirspeed = 140,
            VerticalSpeed = -700,
            IsOnGround = false,
            Phases = new PhaseList { AssignedRunway = runway },
        };
    }

    private sealed record Run(SimulationEngine Engine, AircraftState Aircraft, List<TerminalEntry> Terminal);

    private static bool SimTowerCleared(Run run) => run.Terminal.Any(t => t.Message.StartsWith(SimTowerNote, StringComparison.Ordinal));

    /// <summary>True once the departure has a takeoff clearance, or has left the bar on one.</summary>
    private static bool IsCleared(SimulationEngine engine) =>
        engine.FindAircraft(Callsign) is { } aircraft
        && ((aircraft.Phases?.DepartureClearance is not null) || (aircraft.Phases?.CurrentPhase is not HoldingShortPhase));

    private static bool IsAtTheBar(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase is HoldingShortPhase { HoldShort.Reason: HoldShortReason.DestinationRunway };

    /// <summary>The student's CT to OAK tower through the command path; returns the transfer stamp it left.</summary>
    private static double Transfer(Run run)
    {
        CommandResult ct = run.Engine.SendCommand(Callsign, "CT OAK_TWR");
        Assert.True(ct.Success, ct.Message);
        return run.Aircraft.Ground.LeftStudentFrequencyAtSeconds ?? throw new InvalidOperationException("the CT left no transfer stamp");
    }

    private static void TaxiToTheBar(Run run)
    {
        CommandResult taxi = run.Engine.SendCommand(Callsign, "TAXIAUTO 28R");
        Assert.True(taxi.Success, taxi.Message);
        Assert.True(TickUntil(run.Engine, () => IsAtTheBar(run.Aircraft), 900) > 0, "never reached the bar of its departure runway");
    }

    private Run? LoadAtTheBar(string studentCallsign, string studentType, AtcPositionLevel? lowest) =>
        LoadAtTheBar(studentCallsign, studentType, lowest, _ => []);

    /// <summary>Loads SWA1234 under the student (<see cref="Load"/>, AI staffing from <paramref name="aiStaffed"/>) and taxis it to the bar of 28R.</summary>
    private Run? LoadAtTheBar(
        string studentCallsign,
        string studentType,
        AtcPositionLevel? lowest,
        Func<ArtccConfigRoot, IReadOnlyList<AiPositionConfig>> aiStaffed
    )
    {
        if (Load(studentCallsign, studentType, lowest, withRunwayBlocker: false) is not { } run)
        {
            return null;
        }

        run.Engine.Scenario!.SetAiStaffedPositions(aiStaffed(_zoa!));
        TaxiToTheBar(run);
        return run;
    }

    /// <summary>
    /// Loads SWA1234 on its OAK stand in a solo room whose student works <paramref name="studentCallsign"/> with the lowest
    /// worked level <paramref name="lowest"/>; with <paramref name="withRunwayBlocker"/>, AAL100 also spawns lined up on 28R
    /// with a SAY preset, so it stays there until it is cleared.
    /// </summary>
    private Run? Load(string studentCallsign, string studentType, AtcPositionLevel? lowest, bool withRunwayBlocker)
    {
        var groundData = new TestAirportGroundData();
        if ((_zoa is null) || (groundData.GetLayout("OAK") is null))
        {
            return null;
        }

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => terminal.Add(entry);
        List<string> warnings = engine.LoadScenario(ScenarioJson(withRunwayBlocker), 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        SimScenarioState scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SoloTrainingMode = true;
        PositionConfig student = _zoa.FindPositionByCallsign(studentCallsign, facilityHint: null)!;
        scenario.StudentPosition = _zoa.ResolvePosition(student.Id);
        scenario.StudentPositionType = studentType;
        scenario.StudentLowestWorkedLevel = lowest;
        engine.TickOneSecond();
        AircraftState aircraft = engine.FindAircraft(Callsign) ?? throw new InvalidOperationException($"{Callsign} did not spawn");
        return new Run(engine, aircraft, terminal);
    }

    private static string ScenarioJson(bool withRunwayBlocker)
    {
        string blocker = withRunwayBlocker
            ? """
                ,
                {
                  "id": "a2",
                  "aircraftId": "AAL100",
                  "aircraftType": "B738",
                  "transponderMode": "C",
                  "startingConditions": { "type": "OnRunway", "runway": "28R" },
                  "airportId": "OAK",
                  "flightplan": {
                    "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450,
                    "route": "", "remarks": "", "aircraftType": "B738"
                  },
                  "presetCommands": [ { "id": "p0", "command": "SAY HOLDING IN POSITION", "timeOffset": 0 } ]
                }
                """
            : "";
        return $$"""
            {
              "id": "simulated-tower",
              "name": "Simulated tower departures",
              "artccId": "ZOA",
              "primaryAirportId": "OAK",
              "aircraft": [
                {
                  "id": "a1",
                  "aircraftId": "{{Callsign}}",
                  "aircraftType": "B738",
                  "transponderMode": "C",
                  "startingConditions": { "type": "Parking", "parking": "SIG4" },
                  "flightplan": {
                    "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450,
                    "route": "", "remarks": "", "aircraftType": "B738"
                  },
                  "presetCommands": []
                }{{blocker}}
              ]
            }
            """;
    }

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
