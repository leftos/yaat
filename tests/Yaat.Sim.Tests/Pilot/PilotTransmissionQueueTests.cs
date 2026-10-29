using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

public sealed class PilotTransmissionQueueTests
{
    // The bracket-strip + TTS-normalize behavior is exercised via the surviving string overload,
    // QueueSoloPilotReadback (the transmission string overload was removed with #297 once the
    // follow-up path started carrying both forms independently).
    [Fact]
    public void QueueSoloPilotReadback_SeparatesTerminalTextFromSpeechText()
    {
        AircraftState aircraft = NewAircraft("N123AB");

        PilotResponder.QueueSoloPilotReadback(
            aircraft,
            "[N123AB] tower, november one two three alpha bravo ten-mile final runway two eight right, with information Alpha.",
            PilotResponder.SourceResponse
        );

        Assert.Empty(aircraft.PendingNotifications);
        PilotTransmission tx = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.Equal("N123AB", tx.Callsign);
        // Terminal form: bracketed callsign prefix stripped (SAY column carries it), hyphen kept for display.
        Assert.Equal("tower, november one two three alpha bravo ten-mile final runway two eight right, with information Alpha.", tx.Text);
        // TTS form: bracket stripped and word-joined hyphens de-hyphenated for the speech engine.
        Assert.Equal("tower, november one two three alpha bravo ten mile final runway two eight right, with information Alpha.", tx.SpeechText);
        Assert.Equal(PilotResponder.SourceResponse, tx.SourceKind);
        Assert.Equal(PilotTransmissionKind.SayReadback, tx.Kind);
    }

    [Fact]
    public void QueueSoloPilotReadback_NormalizesHyphenatedTtsTokens()
    {
        AircraftState aircraft = NewAircraft("N9SX");

        PilotResponder.QueueSoloPilotReadback(
            aircraft,
            "[N9SX] tower, november nine sierra x-ray ten-mile final, cleared touch-and-go runway two eight right.",
            PilotResponder.SourceResponse
        );

        PilotTransmission tx = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.Equal("tower, november nine sierra xray ten mile final, cleared touch and go runway two eight right.", tx.SpeechText);
        Assert.DoesNotContain("-ray", tx.SpeechText);
        Assert.DoesNotContain("-mile", tx.SpeechText);
        Assert.DoesNotContain("-and-", tx.SpeechText);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_DrainsTypedQueueToFrequencyQueue()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState aircraft = NewAircraft("SWA123");
        world.AddAircraft(aircraft);
        PilotResponder.QueueSoloPilotReadback(aircraft, "Traffic in sight", PilotResponder.SourceSayReadback);

        List<PilotTransmission> drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 10, rngSeed: 1);

        PilotTransmission tx = Assert.Single(drained);
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
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState proactive = NewAircraft("N200BB");
        AircraftState readback = NewAircraft("N100AA");
        world.AddAircraft(proactive);
        world.AddAircraft(readback);
        QueueSetupTransmission(proactive, "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive);
        QueueSetupTransmission(
            readback,
            "[N100AA] descend and maintain five thousand, november one zero zero alpha alpha.",
            PilotTransmissionKind.Readback
        );
        world.ExpectPilotReadback([.. readback.PendingPilotTransmissions], elapsedSeconds: 10);

        List<PilotTransmission> drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 10, rngSeed: 1);

        PilotTransmission tx = Assert.Single(drained);
        Assert.Equal("N100AA", tx.Callsign);
        Assert.Equal(PilotTransmissionKind.Readback, tx.Kind);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_AwaitedReadbackTimesOut_OtherTransmissionsResume()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState quiet = NewAircraft("N200BB");
        world.AddAircraft(quiet);
        QueueSetupTransmission(quiet, "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive);

        // Controller spoke to N100AA at t=10 but the readback never lands (e.g. aircraft
        // deleted between dispatch and drain). Without a timeout the gate would silence
        // every other pilot forever.
        world.ExpectPilotReadback([NewTransmission("N100AA")], elapsedSeconds: 10);

        List<PilotTransmission> blocked = world.DrainReadyPilotTransmissions(elapsedSeconds: 12, rngSeed: 1);
        Assert.Empty(blocked);

        List<PilotTransmission> released = world.DrainReadyPilotTransmissions(elapsedSeconds: 19, rngSeed: 1);
        PilotTransmission tx = Assert.Single(released);
        Assert.Equal("N200BB", tx.Callsign);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_ProactiveSetsControllerResponseGate_HoldsOtherPilotProactive()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState first = NewAircraft("N100AA");
        AircraftState second = NewAircraft("N200BB");
        world.AddAircraft(first);
        world.AddAircraft(second);
        QueueSetupTransmission(
            first,
            "tower, november one zero zero alpha alpha ten-mile final runway two eight right.",
            PilotTransmissionKind.Proactive
        );

        // First pilot transmits at t=2.
        List<PilotTransmission> firstDrain = world.DrainReadyPilotTransmissions(elapsedSeconds: 2, rngSeed: 1);
        Assert.Single(firstDrain);
        Assert.Equal("N100AA", firstDrain[0].Callsign);

        // Second pilot queues a proactive call at t=5 — should be held back because
        // the controller hasn't responded to the first call yet.
        QueueSetupTransmission(
            second,
            "tower, november two zero zero bravo bravo holding short runway two eight right, ready for departure.",
            PilotTransmissionKind.Proactive
        );

        // First pilot's airtime is ~3.5s, so drain at t=10 is past airtime but
        // the controller-response gate (set at end-of-airtime ~5.5, 8s timeout)
        // is still active until ~13.5s.
        List<PilotTransmission> heldDrain = world.DrainReadyPilotTransmissions(elapsedSeconds: 10, rngSeed: 1);
        Assert.Empty(heldDrain);

        // After airtime + 8s of silence, the gate falls through to FIFO.
        List<PilotTransmission> releasedDrain = world.DrainReadyPilotTransmissions(elapsedSeconds: 14, rngSeed: 1);
        PilotTransmission tx = Assert.Single(releasedDrain);
        Assert.Equal("N200BB", tx.Callsign);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_ControllerResponseGate_ClearedByAcknowledgeControllerResponse()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState first = NewAircraft("N100AA");
        AircraftState second = NewAircraft("N200BB");
        world.AddAircraft(first);
        world.AddAircraft(second);
        QueueSetupTransmission(
            first,
            "tower, november one zero zero alpha alpha ten-mile final runway two eight right.",
            PilotTransmissionKind.Proactive
        );

        world.DrainReadyPilotTransmissions(elapsedSeconds: 2, rngSeed: 1);

        QueueSetupTransmission(
            second,
            "tower, november two zero zero bravo bravo holding short runway two eight right, ready for departure.",
            PilotTransmissionKind.Proactive
        );

        Assert.Empty(world.DrainReadyPilotTransmissions(elapsedSeconds: 6, rngSeed: 1));

        // Controller acknowledges/responds to the first pilot — gate clears.
        world.AcknowledgeControllerResponse("N100AA");

        List<PilotTransmission> released = world.DrainReadyPilotTransmissions(elapsedSeconds: 7, rngSeed: 1);
        PilotTransmission tx = Assert.Single(released);
        Assert.Equal("N200BB", tx.Callsign);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_ControllerResponseGate_DoesNotBlockSamePilotFollowUp()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState pilot = NewAircraft("N100AA");
        world.AddAircraft(pilot);
        QueueSetupTransmission(
            pilot,
            "tower, november one zero zero alpha alpha ten-mile final runway two eight right.",
            PilotTransmissionKind.Proactive
        );

        world.DrainReadyPilotTransmissions(elapsedSeconds: 2, rngSeed: 1);

        // Same pilot follow-up — should not be held back by the gate (it's the awaiting pilot).
        QueueSetupTransmission(pilot, "tower, november one zero zero alpha alpha, did you hear that.", PilotTransmissionKind.Proactive);

        List<PilotTransmission> drain = world.DrainReadyPilotTransmissions(elapsedSeconds: 7, rngSeed: 1);
        PilotTransmission tx = Assert.Single(drain);
        Assert.Equal("N100AA", tx.Callsign);
    }

    [Fact]
    public void DrainReadyPilotTransmissions_ControllerResponseGate_DoesNotBlockReadbacks()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState first = NewAircraft("N100AA");
        AircraftState second = NewAircraft("N200BB");
        world.AddAircraft(first);
        world.AddAircraft(second);
        QueueSetupTransmission(
            first,
            "tower, november one zero zero alpha alpha ten-mile final runway two eight right.",
            PilotTransmissionKind.Proactive
        );

        world.DrainReadyPilotTransmissions(elapsedSeconds: 2, rngSeed: 1);

        // A different aircraft's readback should not be blocked by the controller-response
        // gate — readbacks are responses to controller-issued commands, not new requests.
        QueueSetupTransmission(
            second,
            "[N200BB] descend and maintain five thousand, november two zero zero bravo bravo.",
            PilotTransmissionKind.Readback
        );

        List<PilotTransmission> drain = world.DrainReadyPilotTransmissions(elapsedSeconds: 7, rngSeed: 1);
        PilotTransmission tx = Assert.Single(drain);
        Assert.Equal("N200BB", tx.Callsign);
        Assert.Equal(PilotTransmissionKind.Readback, tx.Kind);
    }

    [Fact]
    public void SendCommand_SoloTraining_SuccessfulDispatchClearsControllerResponseGate()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 2),
        };
        AircraftState first = NewAircraft("N100AA");
        AircraftState second = NewAircraft("N200BB");
        engine.World.AddAircraft(first);
        engine.World.AddAircraft(second);

        // First pilot calls up.
        QueueSetupTransmission(
            first,
            "tower, november one zero zero alpha alpha ten-mile final runway two eight right.",
            PilotTransmissionKind.Proactive
        );
        engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 2, rngSeed: 1);

        // Second pilot queues a proactive — held back by the gate.
        QueueSetupTransmission(
            second,
            "tower, november two zero zero bravo bravo holding short runway two eight right, ready for departure.",
            PilotTransmissionKind.Proactive
        );
        Assert.Empty(engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 6, rngSeed: 1));

        // Controller dispatches a command to the first pilot — gate clears.
        engine.Scenario!.ElapsedSeconds = 6;
        CommandResult result = engine.SendCommand("N100AA", "DM 5000");
        Assert.True(result.Success, result.Message);

        // The readback fires first (it's higher-priority via the readback gate). After
        // its airtime elapses the second pilot's proactive can finally drain.
        List<PilotTransmission> firstResults = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 7, rngSeed: 1);
        Assert.Single(firstResults);
        Assert.Equal("N100AA", firstResults[0].Callsign);
        Assert.Equal(PilotTransmissionKind.Readback, firstResults[0].Kind);

        List<PilotTransmission> released = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 20, rngSeed: 1);
        PilotTransmission tx = Assert.Single(released);
        Assert.Equal("N200BB", tx.Callsign);
    }

    [Fact]
    public void FrequencyActivityMeter_ClassifiesRollingWindow()
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(0, 45);

        Assert.Equal(FrequencyLoadBand.Busy, meter.PeekLoadBand(45));

        meter.Trim(elapsedSeconds: 110);

        Assert.Empty(meter.Transmissions);
        Assert.Equal(FrequencyLoadBand.Quiet, meter.PeekLoadBand(110));
    }

    [Fact]
    public void FrequencyState_GetLoadBand_TrimsBeforeReturning()
    {
        var frequency = new FrequencyState();
        frequency.ActivityMeter.Record(0, 45);

        Assert.Equal(FrequencyLoadBand.Busy, frequency.GetLoadBand(45));
        Assert.Equal(FrequencyLoadBand.Quiet, frequency.GetLoadBand(110));
        Assert.Empty(frequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void TryDequeueReady_RecordsTheCanonicalAirtimeFromTheDequeueSecond()
    {
        var frequency = new FrequencyState();
        frequency.Enqueue(new PilotTransmission("AAL1", "with you", "with you", PilotResponder.SourceResponse, PilotTransmissionKind.Readback, 4.5));

        Assert.NotNull(frequency.TryDequeueReady(30, rngSeed: 1));

        Assert.Equal([new FrequencyTransmission(30, 4.5)], frequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void TryDequeueReady_StampsTheLoadBandAtAirtime()
    {
        var busy = new FrequencyState();
        // [-15, 30]: 45 of the 60 s ending at 30 occupied, 25% dead air.
        busy.ActivityMeter.Record(-15, 45);

        busy.Enqueue(NewTransmission("AAL1"));
        PilotTransmission? busyTransmission = busy.TryDequeueReady(30, rngSeed: 1);

        Assert.NotNull(busyTransmission);
        Assert.Equal(FrequencyLoadBand.Busy, busyTransmission.LoadBand);

        var quiet = new FrequencyState();
        quiet.Enqueue(NewTransmission("AAL2"));
        PilotTransmission? quietTransmission = quiet.TryDequeueReady(30, rngSeed: 1);

        Assert.NotNull(quietTransmission);
        Assert.Equal(FrequencyLoadBand.Quiet, quietTransmission.LoadBand);
    }

    [Fact]
    public void PeekLoadBand_ClassifiesWithoutTrimming()
    {
        var frequency = new FrequencyState();
        frequency.ActivityMeter.Record(-15, 45);

        Assert.Equal(FrequencyLoadBand.Busy, frequency.PeekLoadBand(30));
        Assert.Single(frequency.ActivityMeter.Transmissions);
        Assert.Equal(FrequencyLoadBand.Quiet, frequency.PeekLoadBand(100));
        Assert.Single(frequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void QueuedTransmission_HasNoLoadBandUntilDequeued() => Assert.Null(NewTransmission("AAL1").LoadBand);

    private static PilotTransmission NewTransmission(string callsign) =>
        new(
            callsign,
            "with you",
            "with you",
            PilotResponder.SourceResponse,
            PilotTransmissionKind.Readback,
            FrequencyState.EstimateAirtimeSeconds("with you")
        );

    [Fact]
    public void FrequencyActivityMeter_Restore_ReplacesTheTransmissionsAndTheBand()
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(0, 45);

        meter.Restore([new FrequencyTransmission(1, 2)]);

        Assert.Equal([new FrequencyTransmission(1, 2)], meter.Transmissions);
        Assert.Equal(FrequencyLoadBand.Quiet, meter.PeekLoadBand(3));

        meter.Restore([new FrequencyTransmission(0, 45)]);

        Assert.Equal(FrequencyLoadBand.Busy, meter.PeekLoadBand(45));
    }

    /// <summary>
    /// Fills the window ending at the scenario's second with one transmission of <paramref name="seconds"/> that ends
    /// then, reaching back before the scenario start when the window does.
    /// </summary>
    private static void OccupyFrequency(SimulationEngine engine, double seconds)
    {
        double now = engine.Scenario!.ElapsedSeconds;
        engine.World.ActiveFrequency.ActivityMeter.Record(now - seconds, seconds);
    }

    [Fact]
    public void SendCommand_SoloTraining_UsesVariedReadbackWithCurrentActivityLevel()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);
        // 45 s of the last minute on air, 25% dead air: Busy, which is what selects the terse shortcut asserted below.
        OccupyFrequency(engine, 45);

        CommandResult result = engine.SendCommand("N123AB", "FH 270");

        Assert.True(result.Success, result.Message);
        PilotTransmission transmission = Assert.Single(aircraft.PendingPilotTransmissions);
        // A Busy load band speaks the busy "heading" row (a descent below FL180 has no busy row). Spoken form
        // spells the heading and appends the callsign; the terminal SAY message is the canonical text, compact and callsign-less.
        Assert.Equal("heading two seven zero, november one two three alpha bravo.", transmission.SpeechText);
        Assert.Equal("fly heading 270", transmission.Text);
    }

    [Fact]
    public void SendCommand_SoloTraining_ControllerTransmissionAloneTipsTheReadbackBand()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        // N0004A has the normal trait at seed 1 (PilotTraitsTests.For_IsPinned): it speaks at the meter's own band.
        AircraftState aircraft = NewAircraft("N0004A");
        engine.World.AddAircraft(aircraft);
        // A second whose Normal-band pick is a row other than the canonical (the candidates' first), and one no Quiet
        // candidate speaks, so only the Normal band can have chosen it.
        ParsedCommand command = CommandParser.Parse("FH 270").Value!;
        IReadOnlyList<string> normal = PhraseologyVerbalizer.SpokenCandidates(
            command,
            aircraft,
            FrequencyLoadBand.Normal,
            aircraft.IndicatedAirspeed
        );
        IReadOnlyList<string> quiet = PhraseologyVerbalizer.SpokenCandidates(command, aircraft, FrequencyLoadBand.Quiet, aircraft.IndicatedAirspeed);
        int Pick(int s) => VariantPick.Index("N0004A", "readback-variant:" + normal[0], s, 1, normal.Count);
        int second = Enumerable.Range(30, 200).First(s => Pick(s) != 0);
        string expected = normal[Pick(second)];
        Assert.DoesNotContain(expected, quiet);
        engine.Scenario!.ElapsedSeconds = second;
        // 17.5 s at the start of the window: 70.8% dead air, Quiet before the controller speaks.
        engine.World.ActiveFrequency.ActivityMeter.Record(second - 60, 17.5);
        Assert.Equal(FrequencyLoadBand.Quiet, engine.World.ActiveFrequency.PeekLoadBand(second));

        CommandResult result = engine.SendCommand("N0004A", "FH 270");

        Assert.True(result.Success, result.Message);
        // The controller's FH 270 ends at this second and takes dead air under 70%: the read-back speaks the Normal row,
        // not a Quiet one.
        PilotTransmission transmission = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.StartsWith(expected + ", ", transmission.SpeechText);
        Assert.Equal(FrequencyLoadBand.Normal, engine.World.ActiveFrequency.PeekLoadBand(second));
    }

    [Fact]
    public void SendCommand_SoloTraining_TersePilotAtNormalLoadSpeaksTheBusyShortcut()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        // N0000A is terse at seed 1 (PilotTraitsTests.For_IsPinned): one band busier than the meter.
        AircraftState aircraft = NewAircraft("N0000A");
        engine.World.AddAircraft(aircraft);
        // 25 s on air, the controller's own transmission inside it: 58% dead air, Normal.
        OccupyFrequency(engine, 25);

        CommandResult result = engine.SendCommand("N0000A", "FH 270");

        Assert.True(result.Success, result.Message);
        PilotTransmission transmission = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.StartsWith("heading two seven zero, ", transmission.SpeechText);
        Assert.Equal("fly heading 270", transmission.Text);
    }

    [Fact]
    public void SendCommand_SoloTraining_ChattyPilotAtBusyLoadSpeaksVerbatim()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        // N0003A is chatty at seed 1 (PilotTraitsTests.For_IsPinned): one band quieter than the meter.
        AircraftState aircraft = NewAircraft("N0003A");
        engine.World.AddAircraft(aircraft);
        // 45 s on air, 25% dead air: Busy.
        OccupyFrequency(engine, 45);

        CommandResult result = engine.SendCommand("N0003A", "DM 5000");

        Assert.True(result.Success, result.Message);
        PilotTransmission transmission = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.StartsWith("descend and maintain five thousand, ", transmission.SpeechText);
        Assert.Equal("descend and maintain 5000", transmission.Text);
    }

    [Fact]
    public void SendCommand_SoloTraining_AbbreviatesTheNNumberOnlyAfterTheControllerAcknowledged()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);
        // Two seconds at which this pilot's seeded draw abbreviates its callsign, so only the acknowledgement decides.
        int[] shortSeconds = [.. ShortCallsignSeconds("N123AB", from: 30).Take(2)];

        engine.Scenario!.ElapsedSeconds = shortSeconds[0];
        CommandResult first = engine.SendCommand("N123AB", "DM 5000");
        engine.Scenario!.ElapsedSeconds = shortSeconds[1];
        CommandResult second = engine.SendCommand("N123AB", "FH 270");

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        Assert.Equal(2, aircraft.PendingPilotTransmissions.Count);
        // The readback to the acknowledging command keeps the full callsign; the next one is abbreviated. A quiet row may place
        // the callsign before its closing courtesy, so the form is checked wherever the callsign stands.
        Assert.Contains(", november one two three alpha bravo", aircraft.PendingPilotTransmissions[0].SpeechText);
        Assert.Contains(", november three alpha bravo", aircraft.PendingPilotTransmissions[1].SpeechText);
    }

    [Fact]
    public void SendCommand_SoloTraining_StandbyChainedIntoTheFirstCommandKeepsTheFullCallsign()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);
        engine.Scenario!.ElapsedSeconds = ShortCallsignSeconds("N123AB", from: 30).First();

        CommandResult result = engine.SendCommand("N123AB", "STBY, FH 270");

        Assert.True(result.Success, result.Message);
        // The standby is the acknowledgement itself, so the readback to it is not after one.
        Assert.EndsWith(", november one two three alpha bravo.", aircraft.PendingPilotTransmissions[^1].SpeechText);
    }

    [Theory]
    [InlineData(false, ", november one two three alpha bravo.")]
    [InlineData(true, ", november three alpha bravo.")]
    public void SendCommand_SoloTraining_SimilarCallsignStillOnTheFrequencyKeepsTheFullCallsign(bool otherLeftFrequency, string expectedTail)
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        AircraftState similar = NewAircraft("N456AB");
        similar.HasLeftStudentFrequency = otherLeftFrequency;
        engine.World.AddAircraft(aircraft);
        engine.World.AddAircraft(similar);
        int[] shortSeconds = [.. ShortCallsignSeconds("N123AB", from: 30).Take(2)];

        engine.Scenario!.ElapsedSeconds = shortSeconds[0];
        Assert.True(engine.SendCommand("N123AB", "DM 5000").Success);
        engine.Scenario!.ElapsedSeconds = shortSeconds[1];
        CommandResult result = engine.SendCommand("N123AB", "FH 270");

        Assert.True(result.Success, result.Message);
        Assert.EndsWith(expectedTail, aircraft.PendingPilotTransmissions[^1].SpeechText);
    }

    [Fact]
    public void SendCommand_SoloTraining_FrequencyChangeApprovedAfterAcknowledgementShortens()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);
        int[] shortSeconds = [.. ShortCallsignSeconds("N123AB", from: 30).Take(2)];

        engine.Scenario!.ElapsedSeconds = shortSeconds[0];
        Assert.True(engine.SendCommand("N123AB", "DM 5000").Success);
        engine.Scenario!.ElapsedSeconds = shortSeconds[1];
        CommandResult result = engine.SendCommand("N123AB", "FCA");

        Assert.True(result.Success, result.Message);
        Assert.Contains(aircraft.PendingPilotTransmissions, t => t.SpeechText == "november three alpha bravo, good day.");
    }

    [Fact]
    public void SendCommand_SoloTraining_NoAbbreviatedCallsignAfterContactingTheNextController()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        engine.Scenario!.ArtccConfig = TestArtccConfig.LoadZoa() ?? throw new InvalidOperationException("ZOA snapshot missing");
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);
        int[] shortSeconds = [.. ShortCallsignSeconds("N123AB", from: 30).Take(3)];

        engine.Scenario!.ElapsedSeconds = shortSeconds[0];
        Assert.True(engine.SendCommand("N123AB", "DM 5000").Success);
        engine.Scenario!.ElapsedSeconds = shortSeconds[1];
        Assert.True(engine.SendCommand("N123AB", "CT OAK_TWR").Success);
        engine.Scenario!.ElapsedSeconds = shortSeconds[2];
        CommandResult afterContact = engine.SendCommand("N123AB", "FH 270");

        Assert.True(afterContact.Success, afterContact.Message);
        Assert.True(aircraft.HasLeftStudentFrequency);
        // The contact readback itself is still on the student's frequency, after the acknowledgement.
        PilotTransmission contactReadback = Assert.Single(aircraft.PendingPilotTransmissions, t => t.SpeechText.EndsWith(", so long."));
        Assert.EndsWith(", november three alpha bravo, so long.", contactReadback.SpeechText);
        Assert.EndsWith(", november one two three alpha bravo.", aircraft.PendingPilotTransmissions[^1].SpeechText);
    }

    [Fact]
    public void SendCommand_SoloTraining_BusyContactReadbackGroupsTheFrequency()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        engine.Scenario!.ArtccConfig = TestArtccConfig.LoadZoa() ?? throw new InvalidOperationException("ZOA snapshot missing");
        // N0000A is terse at seed 1 (PilotTraitsTests.For_IsPinned): at a busy meter it speaks at the busy band.
        AircraftState aircraft = NewAircraft("N0000A");
        engine.World.AddAircraft(aircraft);
        int second = Enumerable.Range(30, 200).First(s => SpokenFormDraws.DrawGroupedContactFrequency("N0000A", FrequencyLoadBand.Busy, 1, s));
        engine.Scenario!.ElapsedSeconds = second;
        // 45 s on air: Busy before the controller's CT is counted, which is the band the contact readback reads.
        OccupyFrequency(engine, 45);

        CommandResult result = engine.SendCommand("N0000A", "CT OAK_G_APP");

        Assert.True(result.Success, result.Message);
        PilotTransmission transmission = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.StartsWith("NorCal Approach on twenty five thirty five, ", transmission.SpeechText);
        Assert.Contains("125.35", transmission.Text);
        // The meter takes the canonical read-back's airtime, longer than the grouped frequency the pilot speaks.
        Assert.True(transmission.CanonicalAirtimeSeconds > FrequencyState.EstimateAirtimeSeconds(transmission.SpeechText));
    }

    private static IEnumerable<int> ShortCallsignSeconds(string callsign, int from) =>
        Enumerable.Range(from, 1000).Where(s => SpokenFormDraws.DrawShortCallsign(callsign, rngSeed: 1, elapsedSecond: s));

    [Fact]
    public void SendCommand_NonSolo_DoesNotQueueVariedReadback()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: false, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);

        CommandResult result = engine.SendCommand("N123AB", "DM 5000");

        Assert.True(result.Success, result.Message);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    [Fact]
    public void SendCommand_SoloTraining_CountsTheControllerTransmission()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);

        CommandResult result = engine.SendCommand("N123AB", "DM 5000");

        Assert.True(result.Success, result.Message);
        // The controller's own transmission counts on the same meter, ending at the command's sim time and as long as the
        // canonical read-back of what it said.
        double duration = Assert.Single(aircraft.PendingPilotTransmissions).CanonicalAirtimeSeconds;
        Assert.True(duration > 1.0, $"duration {duration} is the minimum, not the instruction's length");
        Assert.Equal([new FrequencyTransmission(30 - duration, duration)], engine.World.ActiveFrequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void SendCommand_SoloTraining_VariedBusyReadbackRecordsItsCanonicalAirtime()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);
        OccupyFrequency(engine, 45);

        Assert.True(engine.SendCommand("N123AB", "FH 270").Success);
        PilotTransmission queued = Assert.Single(aircraft.PendingPilotTransmissions);
        // The busy pilot speaks the terse "heading two seven zero, ..." but its airtime is the canonical read-back's.
        Assert.True(queued.CanonicalAirtimeSeconds > FrequencyState.EstimateAirtimeSeconds(queued.SpeechText));

        PilotTransmission spoken = Assert.Single(engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 40, rngSeed: 1));

        Assert.Equal(new FrequencyTransmission(40, queued.CanonicalAirtimeSeconds), engine.World.ActiveFrequency.ActivityMeter.Transmissions[^1]);
        Assert.Equal(PilotTransmissionKind.Readback, spoken.Kind);
    }

    [Fact]
    public void SendCommand_SoloTraining_SayAnswerRecordsItsAirtimeFromTheAnswerSecond()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        engine.World.AddAircraft(NewAircraft("N123AB"));
        var answers = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Kind == "SayHeading")
            {
                answers.Add(entry);
            }
        };

        Assert.True(engine.SendCommand("N123AB", "SHDG").Success);

        TerminalEntry answer = Assert.Single(answers);
        // The controller's SHDG ending at the dispatch second, as long as its canonical read-back, and the answer from that
        // second for the airtime of its text.
        double controller = Assert.Single(engine.World.FindAircraft("N123AB")!.PendingPilotTransmissions).CanonicalAirtimeSeconds;
        Assert.Equal(
            [
                new FrequencyTransmission(30 - controller, controller),
                new FrequencyTransmission(30, FrequencyState.EstimateAirtimeSeconds(answer.Message)),
            ],
            engine.World.ActiveFrequency.ActivityMeter.Transmissions.OrderBy(t => t.Start)
        );
    }

    [Fact]
    public void SendCommand_SoloTraining_ControllerTransmissionWithNoReadbackTakesTheOneSecondMinimum()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);

        // STBY is acknowledged with no read-back clause: it verbalizes to nothing and queues no read-back.
        Assert.True(engine.SendCommand("N123AB", "STBY").Success);

        Assert.Empty(aircraft.PendingPilotTransmissions);
        Assert.Equal([new FrequencyTransmission(29, 1.0)], engine.World.ActiveFrequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void SoloTraining_ScenarioTimerSayLineIsNotOnTheMeter()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        engine.Scenario!.ActiveTimers.Add(
            new ActiveTimer
            {
                Id = 1,
                Callsign = null,
                Message = "check the departure list",
                FireAtSeconds = 30,
                TotalSeconds = 30,
            }
        );

        engine.TickPrePhysics();

        TerminalEntry entry = Assert.Single(engine.DrainTerminalEntries(), e => e.Kind == "Say");
        Assert.Equal("TIMER", entry.Callsign);
        Assert.Empty(engine.World.ActiveFrequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void SoloTraining_AircraftScenarioTimerSayLineIsNotOnTheMeter()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        engine.World.AddAircraft(NewAircraft("N123AB"));
        // N123AB TIMER 60 check spacing: the reminder names an aircraft in the world, but nobody says it on the radio.
        engine.Scenario!.ActiveTimers.Add(
            new ActiveTimer
            {
                Id = 1,
                Callsign = "N123AB",
                Message = "check spacing",
                FireAtSeconds = 30,
                TotalSeconds = 60,
            }
        );

        engine.TickPrePhysics();

        TerminalEntry entry = Assert.Single(engine.DrainTerminalEntries(), e => e.Kind == "Say");
        Assert.Equal("N123AB", entry.Callsign);
        Assert.Empty(engine.World.ActiveFrequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void TickOneSecond_SoloTraining_DequeuedReadbackEchoIsMeteredOnce()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);
        PilotTransmission readback = NewTransmission("N123AB");
        aircraft.PendingPilotTransmissions.Add(readback);
        var echoes = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry =>
        {
            if (entry.Callsign == "N123AB")
            {
                echoes.Add(entry);
            }
        };

        engine.TickOneSecond();

        // The bare host echoes the dequeued read-back as a SAY line; the meter holds it once, from the dequeue.
        Assert.StartsWith("Say", Assert.Single(echoes).Kind, StringComparison.Ordinal);
        Assert.Equal([new FrequencyTransmission(31, readback.CanonicalAirtimeSeconds)], engine.World.ActiveFrequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void SendCommand_NonSolo_SayAnswerIsNotOnTheMeter()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: false, elapsedSeconds: 30),
        };
        engine.World.AddAircraft(NewAircraft("N123AB"));

        Assert.True(engine.SendCommand("N123AB", "SHDG").Success);

        Assert.Empty(engine.World.ActiveFrequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void SendCommand_SoloTraining_ChainedCommands_CountOnce()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);

        CommandResult result = engine.SendCommand("N123AB", "DM 5000, FH 270");

        Assert.True(result.Success, result.Message);
        // One transmission however many commands it chains.
        FrequencyTransmission controller = Assert.Single(engine.World.ActiveFrequency.ActivityMeter.Transmissions);
        Assert.Equal(30, controller.End);
    }

    [Fact]
    public void SendCommand_NobodyAnswering_DoesNotCountTheController()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: false, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);

        CommandResult result = engine.SendCommand("N123AB", "DM 5000");

        Assert.True(result.Success, result.Message);
        Assert.Empty(engine.World.ActiveFrequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void SendCommand_SoloTraining_FailedCommand_DoesNotCountTheController()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        aircraft.FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" };
        engine.World.AddAircraft(aircraft);

        CommandResult result = engine.SendCommand("N123AB", "CVA 28R");

        Assert.False(result.Success);
        Assert.Empty(engine.World.ActiveFrequency.ActivityMeter.Transmissions);
    }

    [Fact]
    public void SendCommand_SoloTraining_FailedPilotCommandQueuesUnable()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        aircraft.FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" };
        engine.World.AddAircraft(aircraft);

        // CVA on a VFR aircraft is a dispatch failure (not a parse failure) in an "Approach"-category
        // verb, which is what routes the rejection reason into the pilot's "unable" readback.
        CommandResult result = engine.SendCommand("N123AB", "CVA 28R");

        Assert.False(result.Success);
        Assert.Contains("IFR flight plan", result.Message, StringComparison.OrdinalIgnoreCase);
        PilotTransmission transmission = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.Equal(PilotTransmissionKind.Readback, transmission.Kind);
        Assert.Contains("unable", transmission.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IFR flight plan", transmission.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendCommand_SoloTraining_ParseFailureDoesNotQueueUnable()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(aircraft);

        CommandResult result = engine.SendCommand("N123AB", "NOTACMD");

        Assert.False(result.Success);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    // Queues a drain/gate-test transmission. Reproduces the pre-#297 string-overload behavior
    // (bracket-stripped terminal + normalized TTS) so word-count-derived airtime stays identical
    // to when these tests were written. Text content is not asserted by these tests — only
    // callsign, kind, and drain ordering.
    private static void QueueSetupTransmission(AircraftState aircraft, string text, PilotTransmissionKind kind) =>
        PilotResponder.QueueSoloPilotTransmission(
            aircraft,
            new PilotSpeechText(text, PilotResponder.PrepareForTts(aircraft, text)),
            kind,
            initialContact: false
        );

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
