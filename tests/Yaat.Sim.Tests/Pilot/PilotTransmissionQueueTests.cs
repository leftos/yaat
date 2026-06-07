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
        // Terminal form: bracketed callsign prefix stripped (SAY column carries it), hyphen kept for display.
        Assert.Equal("tower, november one two three alpha bravo ten-mile final runway two eight right, with information Alpha.", tx.Text);
        // TTS form: bracket stripped and word-joined hyphens de-hyphenated for the speech engine.
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
        world.ExpectPilotReadback("N100AA", elapsedSeconds: 10);

        var drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 10);

        var tx = Assert.Single(drained);
        Assert.Equal("N100AA", tx.Callsign);
        Assert.Equal(PilotTransmissionKind.Readback, tx.Kind);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_AwaitedReadbackTimesOut_OtherTransmissionsResume()
    {
        var world = new SimulationWorld();
        var quiet = NewAircraft("N200BB");
        world.AddAircraft(quiet);
        PilotResponder.QueueSoloPilotTransmission(
            quiet,
            "tower, november two zero zero bravo bravo ready to taxi.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );

        // Controller spoke to N100AA at t=10 but the readback never lands (e.g. aircraft
        // deleted between dispatch and drain). Without a timeout the gate would silence
        // every other pilot forever.
        world.ExpectPilotReadback("N100AA", elapsedSeconds: 10);

        var blocked = world.DrainReadyPilotTransmissions(elapsedSeconds: 12);
        Assert.Empty(blocked);

        var released = world.DrainReadyPilotTransmissions(elapsedSeconds: 19);
        var tx = Assert.Single(released);
        Assert.Equal("N200BB", tx.Callsign);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_ProactiveSetsControllerResponseGate_HoldsOtherPilotProactive()
    {
        var world = new SimulationWorld();
        var first = NewAircraft("N100AA");
        var second = NewAircraft("N200BB");
        world.AddAircraft(first);
        world.AddAircraft(second);
        PilotResponder.QueueSoloPilotTransmission(
            first,
            "tower, november one zero zero alpha alpha ten-mile final runway two eight right.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );

        // First pilot transmits at t=2.
        var firstDrain = world.DrainReadyPilotTransmissions(elapsedSeconds: 2);
        Assert.Single(firstDrain);
        Assert.Equal("N100AA", firstDrain[0].Callsign);

        // Second pilot queues a proactive call at t=5 — should be held back because
        // the controller hasn't responded to the first call yet.
        PilotResponder.QueueSoloPilotTransmission(
            second,
            "tower, november two zero zero bravo bravo holding short runway two eight right, ready for departure.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );

        // First pilot's airtime is ~3.5s, so drain at t=10 is past airtime but
        // the controller-response gate (set at end-of-airtime ~5.5, 8s timeout)
        // is still active until ~13.5s.
        var heldDrain = world.DrainReadyPilotTransmissions(elapsedSeconds: 10);
        Assert.Empty(heldDrain);

        // After airtime + 8s of silence, the gate falls through to FIFO.
        var releasedDrain = world.DrainReadyPilotTransmissions(elapsedSeconds: 14);
        var tx = Assert.Single(releasedDrain);
        Assert.Equal("N200BB", tx.Callsign);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_ControllerResponseGate_ClearedByAcknowledgeControllerResponse()
    {
        var world = new SimulationWorld();
        var first = NewAircraft("N100AA");
        var second = NewAircraft("N200BB");
        world.AddAircraft(first);
        world.AddAircraft(second);
        PilotResponder.QueueSoloPilotTransmission(
            first,
            "tower, november one zero zero alpha alpha ten-mile final runway two eight right.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );

        world.DrainReadyPilotTransmissions(elapsedSeconds: 2);

        PilotResponder.QueueSoloPilotTransmission(
            second,
            "tower, november two zero zero bravo bravo holding short runway two eight right, ready for departure.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );

        Assert.Empty(world.DrainReadyPilotTransmissions(elapsedSeconds: 6));

        // Controller acknowledges/responds to the first pilot — gate clears.
        world.AcknowledgeControllerResponse("N100AA");

        var released = world.DrainReadyPilotTransmissions(elapsedSeconds: 7);
        var tx = Assert.Single(released);
        Assert.Equal("N200BB", tx.Callsign);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_ControllerResponseGate_DoesNotBlockSamePilotFollowUp()
    {
        var world = new SimulationWorld();
        var pilot = NewAircraft("N100AA");
        world.AddAircraft(pilot);
        PilotResponder.QueueSoloPilotTransmission(
            pilot,
            "tower, november one zero zero alpha alpha ten-mile final runway two eight right.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );

        world.DrainReadyPilotTransmissions(elapsedSeconds: 2);

        // Same pilot follow-up — should not be held back by the gate (it's the awaiting pilot).
        PilotResponder.QueueSoloPilotTransmission(
            pilot,
            "tower, november one zero zero alpha alpha, did you hear that.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );

        var drain = world.DrainReadyPilotTransmissions(elapsedSeconds: 7);
        var tx = Assert.Single(drain);
        Assert.Equal("N100AA", tx.Callsign);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_ControllerResponseGate_DoesNotBlockReadbacks()
    {
        var world = new SimulationWorld();
        var first = NewAircraft("N100AA");
        var second = NewAircraft("N200BB");
        world.AddAircraft(first);
        world.AddAircraft(second);
        PilotResponder.QueueSoloPilotTransmission(
            first,
            "tower, november one zero zero alpha alpha ten-mile final runway two eight right.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );

        world.DrainReadyPilotTransmissions(elapsedSeconds: 2);

        // A different aircraft's readback should not be blocked by the controller-response
        // gate — readbacks are responses to controller-issued commands, not new requests.
        PilotResponder.QueueSoloPilotTransmission(
            second,
            "[N200BB] descend and maintain five thousand, november two zero zero bravo bravo.",
            PilotTransmissionKind.Readback,
            PilotResponder.SourceResponse
        );

        var drain = world.DrainReadyPilotTransmissions(elapsedSeconds: 7);
        var tx = Assert.Single(drain);
        Assert.Equal("N200BB", tx.Callsign);
        Assert.Equal(PilotTransmissionKind.Readback, tx.Kind);
    }

    [Fact]
    public void SendCommand_SoloTraining_SuccessfulDispatchClearsControllerResponseGate()
    {
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 2) };
        var first = NewAircraft("N100AA");
        var second = NewAircraft("N200BB");
        engine.World.AddAircraft(first);
        engine.World.AddAircraft(second);

        // First pilot calls up.
        PilotResponder.QueueSoloPilotTransmission(
            first,
            "tower, november one zero zero alpha alpha ten-mile final runway two eight right.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );
        engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 2);

        // Second pilot queues a proactive — held back by the gate.
        PilotResponder.QueueSoloPilotTransmission(
            second,
            "tower, november two zero zero bravo bravo holding short runway two eight right, ready for departure.",
            PilotTransmissionKind.Proactive,
            PilotResponder.SourceResponse
        );
        Assert.Empty(engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 6));

        // Controller dispatches a command to the first pilot — gate clears.
        engine.Scenario!.ElapsedSeconds = 6;
        var result = engine.SendCommand("N100AA", "DM 5000");
        Assert.True(result.Success, result.Message);

        // The readback fires first (it's higher-priority via the readback gate). After
        // its airtime elapses the second pilot's proactive can finally drain.
        var firstResults = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 7);
        Assert.Single(firstResults);
        Assert.Equal("N100AA", firstResults[0].Callsign);
        Assert.Equal(PilotTransmissionKind.Readback, firstResults[0].Kind);

        var released = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 20);
        var tx = Assert.Single(released);
        Assert.Equal("N200BB", tx.Callsign);
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
        // Saturated activity level uses the terse "down to" shortcut. Spoken form spells the
        // altitude and appends the callsign; the terminal SAY message is compact and callsign-less.
        Assert.Equal("down to five thousand, november one two three alpha bravo.", transmission.SpeechText);
        Assert.Equal("down to 5000", transmission.Text);
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

    [Fact]
    public void SendCommand_SoloTraining_FailedPilotCommandQueuesUnable()
    {
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30) };
        var aircraft = NewAircraft("N123AB");
        aircraft.FlightPlan = new AircraftFlightPlan { FlightRules = "IFR" };
        engine.World.AddAircraft(aircraft);

        var result = engine.SendCommand("N123AB", "CM A025");

        Assert.False(result.Success);
        Assert.Contains("VFR aircraft", result.Message, StringComparison.OrdinalIgnoreCase);
        var transmission = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.Equal(PilotTransmissionKind.Readback, transmission.Kind);
        Assert.Contains("unable", transmission.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VFR aircraft", transmission.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendCommand_SoloTraining_ParseFailureDoesNotQueueUnable()
    {
        var engine = new SimulationEngine(new TestAirportGroundData()) { Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30) };
        var aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);

        var result = engine.SendCommand("N123AB", "NOTACMD");

        Assert.False(result.Success);
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
