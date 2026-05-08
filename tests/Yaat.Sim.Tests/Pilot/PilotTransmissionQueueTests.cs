using Xunit;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

public sealed class PilotTransmissionQueueTests
{
    [Fact]
    public void QueueSoloPilotTransmission_SeparatesTerminalTextFromSpeechText()
    {
        var aircraft = NewAircraft("N123AB");

        PilotResponder.QueueSoloPilotTransmission(
            aircraft,
            "[N123AB] tower, november one two three alpha bravo ten-mile final runway two eight right, with information Alpha.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );

        Assert.Empty(aircraft.PendingNotifications);
        var tx = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.Equal("N123AB", tx.Callsign);
        Assert.Equal("[N123AB] tower, november one two three alpha bravo ten-mile final runway two eight right, with information Alpha.", tx.Text);
        Assert.Equal("tower, november one two three alpha bravo ten mile final runway two eight right, with information Alpha.", tx.SpeechText);
        Assert.Equal(PilotResponder.SourceResponse, tx.SourceKind);
        Assert.Equal(PilotTransmissionKind.Proactive, tx.Kind);
    }

    [Fact]
    public void QueueSoloPilotTransmission_NormalizesHyphenatedTtsTokens()
    {
        var aircraft = NewAircraft("N9SX");

        PilotResponder.QueueSoloPilotTransmission(
            aircraft,
            "[N9SX] tower, november nine sierra x-ray ten-mile final, cleared touch-and-go runway two eight right.",
            PilotTransmissionKind.Readback,
            PilotResponder.SourceResponse
        );

        var tx = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.Equal("tower, november nine sierra xray ten mile final, cleared touch and go runway two eight right.", tx.SpeechText);
        Assert.DoesNotContain("-ray", tx.SpeechText);
        Assert.DoesNotContain("-mile", tx.SpeechText);
        Assert.DoesNotContain("-and-", tx.SpeechText);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_DrainsTypedQueueToFrequencyQueue()
    {
        var world = new SimulationWorld();
        var aircraft = NewAircraft("SWA123");
        world.AddAircraft(aircraft);
        PilotResponder.QueueSoloPilotReadback(aircraft, "Traffic in sight", PilotResponder.SourceSayReadback);

        var drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 10);

        var tx = Assert.Single(drained);
        Assert.Equal("SWA123", tx.Callsign);
        Assert.Equal("Traffic in sight", tx.Text);
        Assert.Equal("Traffic in sight", tx.SpeechText);
        Assert.Equal(PilotTransmissionKind.SayReadback, tx.Kind);
        Assert.Empty(aircraft.PendingPilotTransmissions);
        Assert.Empty(aircraft.PendingPilotReadbacks);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_PrioritizesAwaitedReadback()
    {
        var world = new SimulationWorld();
        var proactive = NewAircraft("N200BB");
        var readback = NewAircraft("N100AA");
        world.AddAircraft(proactive);
        world.AddAircraft(readback);
        PilotResponder.QueueSoloPilotTransmission(
            proactive,
            "tower, november two zero zero bravo bravo ready to taxi.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );
        PilotResponder.QueueSoloPilotTransmission(
            readback,
            "[N100AA] descend and maintain five thousand, november one zero zero alpha alpha.",
            PilotTransmissionKind.Readback,
            PilotResponder.SourceResponse
        );
        world.ExpectPilotReadback("N100AA");

        var drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 10);

        var tx = Assert.Single(drained);
        Assert.Equal("N100AA", tx.Callsign);
        Assert.Equal(PilotTransmissionKind.Readback, tx.Kind);
    }

    [Fact]
    public void FrequencyActivityMeter_ClassifiesRollingWindow()
    {
        var meter = new FrequencyActivityMeter();
        for (int i = 0; i < 21; i++)
        {
            meter.Record(i);
        }

        Assert.Equal(FrequencyActivityLevel.Saturated, meter.Level);

        meter.Trim(elapsedSeconds: 80);

        Assert.Equal(FrequencyActivityLevel.Quiet, meter.Level);
    }

    [Fact]
    public void FrequencyState_GetActivityLevel_TrimsBeforeReturning()
    {
        var frequency = new FrequencyState();
        for (int i = 0; i < 21; i++)
        {
            frequency.ActivityMeter.Record(i);
        }

        Assert.Equal(FrequencyActivityLevel.Saturated, frequency.GetActivityLevel(21));
        Assert.Equal(FrequencyActivityLevel.Quiet, frequency.GetActivityLevel(80));
    }

    [Fact]
    public void SendCommand_SoloTraining_UsesVariedReadbackWithCurrentActivityLevel()
    {
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30) };
        var aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);
        for (int i = 0; i < 21; i++)
        {
            engine.World.ActiveFrequency.ActivityMeter.Record(i);
        }

        var result = engine.SendCommand("N123AB", "DM 5000");

        Assert.True(result.Success, result.Message);
        var transmission = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.Equal("[N123AB] down to five thousand, november one two three alpha bravo.", transmission.Text);
    }

    [Fact]
    public void SendCommand_NonSolo_DoesNotQueueVariedReadback()
    {
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(soloTrainingMode: false, elapsedSeconds: 30) };
        var aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);

        var result = engine.SendCommand("N123AB", "DM 5000");

        Assert.True(result.Success, result.Message);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    private static AircraftState NewAircraft(string callsign) => new() { Callsign = callsign, AircraftType = "C172" };

    private static SimScenarioState NewScenario(bool soloTrainingMode, double elapsedSeconds) =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = elapsedSeconds,
            SoloTrainingMode = soloTrainingMode,
        };
}
