using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Pins the read-back gate: a controller's instruction is followed by that pilot's read-back before any other pilot
/// speaks (YAAT-1148). Every path here is a way the gate could leak a call from another pilot in between.
/// </summary>
public sealed class ReadbackGateLeakTests
{
    /// <summary>Forty words at 0.25 s each: one transmission of airtime long enough to outlast the 8 s window.</summary>
    private const string FortyWordTransmission =
        "november nine zero zero zulu zulu descend and maintain five thousand one hundred twenty two thousand feet and say your altitude and heading "
        + "and speed when able and confirm you have information alpha bravo charlie delta echo fox trot yankee";

    [Fact]
    public void ReadbackGate_HoldsOtherProactive_UntilAwaitedReadbackDequeues()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState awaited = NewAircraft("N100AA");
        AircraftState other = NewAircraft("N200BB");
        world.AddAircraft(other);
        world.AddAircraft(awaited);

        PilotTransmission readback = Transmission(
            "N100AA",
            "[N100AA] descend and maintain five thousand, november one zero zero alpha alpha.",
            PilotTransmissionKind.Readback
        );
        world.ExpectPilotReadback([readback], elapsedSeconds: 10);
        QueueSetupTransmission(other, "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive);

        // The instructed read-back has not been queued at all yet: the other pilot is held for the window.
        Assert.Empty(world.DrainReadyPilotTransmissions(elapsedSeconds: 12));

        awaited.PendingPilotTransmissions.Add(readback);
        List<PilotTransmission> drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 13);
        AssertSpoke(readback, drained);
        Assert.Null(world.ActiveFrequency.AwaitingReadbackFrom);

        // With the read-back on air, the held call follows once its airtime is over.
        List<PilotTransmission> released = world.DrainReadyPilotTransmissions(elapsedSeconds: 30);
        Assert.Equal("N200BB", Assert.Single(released).Callsign);
    }

    [Fact]
    public void ReadbackGate_ReadbackStuckBehindAirtime_BeyondEightSeconds_DoesNotLetOtherPilotIn()
    {
        var frequency = new FrequencyState();
        frequency.Enqueue(Transmission("N900ZZ", FortyWordTransmission, PilotTransmissionKind.Readback));

        // Forty words hold the frequency until t=10; nothing can speak before then.
        Assert.Equal("N900ZZ", frequency.TryDequeueReady(elapsedSeconds: 0)?.Callsign);
        PilotTransmission stalled = Transmission(
            "N100AA",
            "[N100AA] descend and maintain five thousand, november one zero zero alpha alpha.",
            PilotTransmissionKind.Readback
        );
        frequency.ExpectReadback([stalled], elapsedSeconds: 1);
        frequency.Enqueue(Transmission("N200BB", "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive));

        Assert.Null(frequency.TryDequeueReady(elapsedSeconds: 9));
        // Eight seconds past the instruction the airtime is only just free: the read-back's window must not have burned.
        Assert.Null(frequency.TryDequeueReady(elapsedSeconds: 10));

        frequency.Enqueue(stalled);
        PilotTransmission? readback = frequency.TryDequeueReady(elapsedSeconds: 10.5);

        Assert.NotNull(readback);
        Assert.Equal(stalled.Text, readback.Text);
        Assert.Equal("N200BB", frequency.TryDequeueReady(elapsedSeconds: 40)?.Callsign);
    }

    [Fact]
    public void ReadbackGate_TwoInstructionsSameTick_BothReadbacksPrecedeThirdPilotsEarlierQueuedCall()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 10),
        };
        AircraftState third = NewAircraft("N300CC");
        AircraftState first = NewAircraft("N100AA");
        AircraftState second = NewAircraft("N200BB");
        // Aircraft order fixes the frequency queue order: N300CC's call is enqueued before either read-back.
        engine.World.AddAircraft(third);
        engine.World.AddAircraft(first);
        engine.World.AddAircraft(second);
        QueueSetupTransmission(third, "tower, november three zero zero charlie charlie ready to taxi.", PilotTransmissionKind.Proactive);

        Assert.True(engine.SendCommand("N100AA", "FH 270").Success);
        Assert.True(engine.SendCommand("N200BB", "DM 5000").Success);
        Assert.Equal("N100AA", engine.World.ActiveFrequency.AwaitingReadbackFrom);

        List<PilotTransmission> firstDrain = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 10);
        PilotTransmission spoke = Assert.Single(firstDrain);
        Assert.Equal("N100AA", spoke.Callsign);
        Assert.Equal("fly heading 270", spoke.Text);

        List<PilotTransmission> secondDrain = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 40);
        Assert.Equal("N200BB", Assert.Single(secondDrain).Callsign);

        // Only once both read-backs have spoken does the third pilot's earlier-queued call get through.
        List<PilotTransmission> thirdDrain = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 80);
        Assert.Equal("N300CC", Assert.Single(thirdDrain).Callsign);
    }

    [Fact]
    public void ReadbackGate_InstructionWhoseReadbackNeverQueues_ReleasesAtEightSeconds()
    {
        var frequency = new FrequencyState();
        PilotTransmission neverQueued = Transmission(
            "N100AA",
            "[N100AA] fly heading 270, november one zero zero alpha alpha.",
            PilotTransmissionKind.Readback
        );
        frequency.ExpectReadback([neverQueued], elapsedSeconds: 10);
        frequency.Enqueue(Transmission("N200BB", "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive));

        Assert.Null(frequency.TryDequeueReady(elapsedSeconds: 12));
        Assert.Null(frequency.TryDequeueReady(elapsedSeconds: 17));
        Assert.Equal("N100AA", frequency.AwaitingReadbackFrom);

        PilotTransmission? released = frequency.TryDequeueReady(elapsedSeconds: 18);

        Assert.Equal("N200BB", released?.Callsign);
        Assert.Null(frequency.AwaitingReadbackFrom);
    }

    [Fact]
    public void ReadbackGate_ExpectThenDrainInSameTick()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState awaited = NewAircraft("N100AA");
        AircraftState other = NewAircraft("N200BB");
        world.AddAircraft(other);
        world.AddAircraft(awaited);

        PilotTransmission readback = QueueSetupTransmission(
            awaited,
            "[N100AA] fly heading 270, november one zero zero alpha alpha.",
            PilotTransmissionKind.Readback
        );
        world.ExpectPilotReadback([readback], elapsedSeconds: 5);
        QueueSetupTransmission(other, "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive);

        List<PilotTransmission> drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 5);

        AssertSpoke(readback, drained);
        Assert.Null(world.ActiveFrequency.AwaitingReadbackFrom);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ReadbackGate_ReactionDelayedInstruction_ArmsTheGate(int reactionDelaySeconds)
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        engine.Scenario!.CommandRunDelayMinSeconds = reactionDelaySeconds;
        engine.Scenario.CommandRunDelayMaxSeconds = reactionDelaySeconds;
        engine.World.ReactionDelayRng = new SerializableRandom(42);
        AircraftState other = NewAircraft("N200BB");
        AircraftState instructed = NewAircraft("N100AA");
        engine.World.AddAircraft(other);
        engine.World.AddAircraft(instructed);
        QueueSetupTransmission(other, "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive);

        CommandResult result = engine.SendCommand("N100AA", "FH 270");

        Assert.True(result.Success, result.Message);
        // The delay row really defers the command; the 0 row does not. The read-back is queued at issue time either way.
        Assert.Equal(reactionDelaySeconds > 0 ? 1 : 0, instructed.DeferredDispatches.Count);
        PilotTransmission readback = Assert.Single(instructed.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("N100AA", engine.World.ActiveFrequency.AwaitingReadbackFrom);

        List<PilotTransmission> first = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 30);
        AssertSpoke(readback, first);

        List<PilotTransmission> second = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 60);
        Assert.Equal("N200BB", Assert.Single(second).Callsign);
    }

    [Fact]
    public void ReadbackGate_UnableReadbackArmsTheGate()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState unable = NewAircraft("N123AB");
        unable.FlightPlan = new AircraftFlightPlan { FlightRules = "VFR" };
        engine.World.AddAircraft(unable);

        // CVA on a VFR aircraft is a dispatch failure carrying a pilot "unable" read-back.
        CommandResult result = engine.SendCommand("N123AB", "CVA 28R");

        Assert.False(result.Success);
        Assert.Contains(unable.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("N123AB", engine.World.ActiveFrequency.AwaitingReadbackFrom);
    }

    [Fact]
    public void ReadbackGate_ContactReadbackArmsTheGate()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        engine.Scenario!.ArtccConfig = TestArtccConfig.LoadZoa() ?? throw new InvalidOperationException("ZOA snapshot missing");
        AircraftState contact = NewAircraft("N123AB");
        engine.World.AddAircraft(contact);

        CommandResult result = engine.SendCommand("N123AB", "CT OAK_TWR");

        Assert.True(result.Success, result.Message);
        Assert.Contains(contact.PendingPilotTransmissions, t => t.Kind == PilotTransmissionKind.Readback);
        Assert.Equal("N123AB", engine.World.ActiveFrequency.AwaitingReadbackFrom);
    }

    [Fact]
    public void ReadbackGate_PilotInitiatedReadbackPendingAtDispatch_DoesNotArmForANonReadbackCommand()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState other = NewAircraft("N200BB");
        AircraftState aircraft = NewAircraft("N123AB");
        engine.World.AddAircraft(other);
        engine.World.AddAircraft(aircraft);

        // The pilot speaks on its own: the "unable" it announces on an assigned altitude (PilotProactive queues it as a
        // Readback with no controller instruction behind it). It is still pending when the controller's next dispatch runs.
        QueueSetupTransmission(aircraft, "unable five thousand, november one two three alpha bravo.", PilotTransmissionKind.Readback);
        // A second pilot's call was queued before it, so FIFO puts that call first.
        QueueSetupTransmission(other, "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive);

        // STBY is acknowledged with no read-back clause of its own, so this dispatch queues nothing.
        Assert.True(engine.SendCommand("N123AB", "STBY").Success);
        Assert.Single(aircraft.PendingPilotTransmissions);

        // The pilot's own message is not a reply to the instruction, so it arms no gate.
        Assert.Null(engine.World.ActiveFrequency.AwaitingReadbackFrom);

        List<PilotTransmission> drained = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 30);
        PilotTransmission first = Assert.Single(drained);
        Assert.Equal("N200BB", first.Callsign);
        Assert.Equal(PilotTransmissionKind.Proactive, first.Kind);
    }

    [Fact]
    public void ReadbackGate_PilotsOwnReadbackPending_DoesNotSatisfyTheInstructionsGate()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState other = NewAircraft("N200BB");
        AircraftState aircraft = NewAircraft("N100AA");
        engine.World.AddAircraft(other);
        engine.World.AddAircraft(aircraft);

        // The pilot's own message is pending when the controller instructs it; the other pilot's call is queued ahead.
        PilotTransmission own = QueueSetupTransmission(
            aircraft,
            "unable five thousand, november one zero zero alpha alpha.",
            PilotTransmissionKind.Readback
        );
        QueueSetupTransmission(other, "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive);

        Assert.True(engine.SendCommand("N100AA", "FH 270").Success);
        PilotTransmission instructionReadback = Assert.Single(aircraft.PendingPilotTransmissions, t => t.Text == "fly heading 270");
        Assert.Equal("N100AA", engine.World.ActiveFrequency.AwaitingReadbackFrom);

        // The instruction's read-back takes the slot, not the pilot's own earlier message.
        List<PilotTransmission> first = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 30);
        AssertSpoke(instructionReadback, first);

        // The other pilot speaks next: nothing between the two read-backs got in.
        List<PilotTransmission> second = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 60);
        Assert.Equal("N200BB", Assert.Single(second).Callsign);

        List<PilotTransmission> third = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 90);
        AssertSpoke(own, third);
    }

    [Fact]
    public void ReadbackGate_TwoInstructionsToOneAircraft_BothReadbacksGated()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = NewScenario(soloTrainingMode: true, elapsedSeconds: 30),
        };
        AircraftState other = NewAircraft("N200BB");
        AircraftState aircraft = NewAircraft("N100AA");
        engine.World.AddAircraft(other);
        engine.World.AddAircraft(aircraft);
        QueueSetupTransmission(other, "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive);

        // A second instruction lands before the first read-back has aired.
        Assert.True(engine.SendCommand("N100AA", "FH 270").Success);
        Assert.True(engine.SendCommand("N100AA", "DM 5000").Success);
        PilotTransmission heading = Assert.Single(aircraft.PendingPilotTransmissions, t => t.Text == "fly heading 270");
        PilotTransmission descend = Assert.Single(aircraft.PendingPilotTransmissions, t => t.Text == "descend and maintain 5000");

        List<PilotTransmission> first = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 30);
        AssertSpoke(heading, first);

        // The second read-back keeps its own gate entry; the other pilot does not slip in between.
        List<PilotTransmission> second = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 60);
        AssertSpoke(descend, second);

        List<PilotTransmission> third = engine.World.DrainReadyPilotTransmissions(elapsedSeconds: 90);
        Assert.Equal("N200BB", Assert.Single(third).Callsign);
    }

    [Fact]
    public void ReadbackGate_RemovedAircraft_ReleasesTheGateAtOnce()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState awaited = NewAircraft("N100AA");
        AircraftState other = NewAircraft("N200BB");
        world.AddAircraft(awaited);
        world.AddAircraft(other);

        PilotTransmission readback = Transmission(
            "N100AA",
            "[N100AA] fly heading 270, november one zero zero alpha alpha.",
            PilotTransmissionKind.Readback
        );
        world.ExpectPilotReadback([readback], elapsedSeconds: 5);
        QueueSetupTransmission(other, "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive);

        // The read-back was never queued and its aircraft is gone: there is nothing left to wait for.
        world.RemoveAircraft("N100AA");

        List<PilotTransmission> drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 5);

        Assert.Equal("N200BB", Assert.Single(drained).Callsign);
        Assert.Null(world.ActiveFrequency.AwaitingReadbackFrom);
    }

    [Fact]
    public void ReadbackGate_SameCallsignReplacement_ReleasesTheGate()
    {
        var world = new SimulationWorld(PushPlanStore.CreateShipped());
        AircraftState awaited = NewAircraft("N100AA");
        AircraftState other = NewAircraft("N200BB");
        world.AddAircraft(awaited);
        world.AddAircraft(other);

        PilotTransmission readback = Transmission(
            "N100AA",
            "[N100AA] fly heading 270, november one zero zero alpha alpha.",
            PilotTransmissionKind.Readback
        );
        world.ExpectPilotReadback([readback], elapsedSeconds: 5);
        QueueSetupTransmission(other, "tower, november two zero zero bravo bravo ready to taxi.", PilotTransmissionKind.Proactive);

        // A same-callsign spawn replaces N100AA: the replaced aircraft's read-back can never arrive either.
        world.AddAircraft(NewAircraft("N100AA"));

        List<PilotTransmission> drained = world.DrainReadyPilotTransmissions(elapsedSeconds: 5);

        Assert.Equal("N200BB", Assert.Single(drained).Callsign);
        Assert.Null(world.ActiveFrequency.AwaitingReadbackFrom);
    }

    /// <summary>
    /// Asserts that exactly one transmission drained and that it is <paramref name="expected"/> (the queue hands back a
    /// copy carrying its load band).
    /// </summary>
    private static void AssertSpoke(PilotTransmission expected, List<PilotTransmission> drained)
    {
        PilotTransmission spoken = Assert.Single(drained);
        Assert.Equal(expected.Callsign, spoken.Callsign);
        Assert.Equal(expected.Text, spoken.Text);
        Assert.Equal(expected.Kind, spoken.Kind);
    }

    private static PilotTransmission Transmission(string callsign, string text, PilotTransmissionKind kind) =>
        new(callsign, text, text, PilotResponder.SourceResponse, kind, FrequencyState.EstimateAirtimeSeconds(text));

    private static PilotTransmission QueueSetupTransmission(AircraftState aircraft, string text, PilotTransmissionKind kind)
    {
        string speech = PilotResponder.PrepareForTts(aircraft, text);
        PilotTransmission transmission = new(
            aircraft.Callsign,
            text,
            speech,
            PilotResponder.SourceResponse,
            kind,
            FrequencyState.EstimateAirtimeSeconds(speech)
        );
        aircraft.PendingPilotTransmissions.Add(transmission);
        return transmission;
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
