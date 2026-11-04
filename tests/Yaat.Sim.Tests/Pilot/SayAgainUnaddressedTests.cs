using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The student's unaddressed "say again" (<c>AGAINU</c>, <c>AGAINU CS</c>), sent with no callsign: the pilot whose call
/// still awaits a controller response repeats, else the last pilot to transmit, ties going to the ordinal-lower callsign.
/// An aircraft that has left the student's frequency is never chosen, and with no pilot to choose the command fails and
/// queues nothing. The result names the chosen callsign.
/// </summary>
public sealed class SayAgainUnaddressedTests
{
    private const string First = "N123AB";
    private const string Second = "N456CD";

    public SayAgainUnaddressedTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Unaddressed_PicksTheAircraftAwaitingAResponse()
    {
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        AircraftState first = SayAgainTests.AddAirborne(engine, First);
        AircraftState second = SayAgainTests.AddAirborne(engine, Second);
        QueueProactive(second, "tower, november four five six charlie delta, eight miles east, inbound landing.");
        PilotTransmission call = Assert.Single(Drain(engine, 10));
        Assert.Equal(Second, engine.World.ActiveFrequency.AwaitingControllerResponseTo);
        // The other aircraft spoke after that call, so it is the last to transmit; the awaited call still wins.
        first.LastPilotTransmission = LastLine(first, "fly heading 270", atSeconds: 20);

        Assert.True(engine.SendCommand("", "AGAINU").Success);

        PilotTransmission repeat = Assert.Single(Drain(engine, 70));
        Assert.True(repeat.IsRepeat);
        Assert.Equal(Second, repeat.Callsign);
        Assert.Equal(call.Text, repeat.Text);
    }

    [Fact]
    public void Unaddressed_WithNoAwaitedCall_PicksTheLastToTransmit()
    {
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        SayAgainTests.AddAirborne(engine, First);
        SayAgainTests.AddAirborne(engine, Second);
        Assert.True(engine.SendCommand(First, "FH 270").Success);
        Assert.Single(Drain(engine, 10));
        Assert.True(engine.SendCommand(Second, "FH 090").Success);
        Assert.Single(Drain(engine, 20));
        Assert.Null(engine.World.ActiveFrequency.AwaitingControllerResponseTo);

        Assert.True(engine.SendCommand("", "AGAINU").Success);

        PilotTransmission repeat = Assert.Single(Drain(engine, 40));
        Assert.True(repeat.IsRepeat);
        Assert.Equal(Second, repeat.Callsign);
        Assert.Equal("fly heading 090", repeat.Text);
    }

    [Fact]
    public void Unaddressed_TieOnTime_PicksTheLowerCallsign()
    {
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        // Added in reverse order, so the world's order cannot be what breaks the tie.
        AircraftState second = SayAgainTests.AddAirborne(engine, Second);
        AircraftState first = SayAgainTests.AddAirborne(engine, First);
        second.LastPilotTransmission = LastLine(second, "fly heading 090", atSeconds: 10);
        first.LastPilotTransmission = LastLine(first, "fly heading 270", atSeconds: 10);

        Assert.True(engine.SendCommand("", "AGAINU").Success);

        PilotTransmission repeat = Assert.Single(Drain(engine, 30));
        Assert.Equal(First, repeat.Callsign);
        Assert.Equal("fly heading 270", repeat.Text);
    }

    [Fact]
    public void Unaddressed_SkipsAnAircraftThatLeftTheFrequency()
    {
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        AircraftState first = SayAgainTests.AddAirborne(engine, First);
        AircraftState second = SayAgainTests.AddAirborne(engine, Second);
        first.LastPilotTransmission = LastLine(first, "fly heading 270", atSeconds: 5);
        QueueProactive(second, "tower, november four five six charlie delta, eight miles east, inbound landing.");
        Assert.Single(Drain(engine, 10));
        Assert.Equal(Second, engine.World.ActiveFrequency.AwaitingControllerResponseTo);
        // Awaited and the last to transmit, but no longer on the student's frequency.
        second.HasLeftStudentFrequency = true;

        CommandResult result = engine.SendCommand("", "AGAINU");

        Assert.True(result.Success, result.Message);
        Assert.False(second.PendingRepeat);
        PilotTransmission repeat = Assert.Single(Drain(engine, 70));
        Assert.Equal(First, repeat.Callsign);
        Assert.Equal("fly heading 270", repeat.Text);
    }

    [Theory]
    [InlineData("AGAINU")]
    [InlineData("AGAINU CS")]
    public void Unaddressed_NoEligibleAircraft_FailsAndQueuesNothing(string command)
    {
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        AircraftState aircraft = SayAgainTests.AddAirborne(engine, First);
        int pendingBefore = engine.World.ActiveFrequency.PendingCount;

        CommandResult result = engine.SendCommand("", command);

        Assert.False(result.Success);
        Assert.Equal("Say again: no pilot has called", result.Message);
        Assert.Equal(pendingBefore, engine.World.ActiveFrequency.PendingCount);
        Assert.False(aircraft.PendingRepeat || aircraft.PendingRepeatCallsignOnly);
        Assert.Empty(Drain(engine, 30));
    }

    [Fact]
    public void UnaddressedCs_RepeatsOnlyTheCallsign()
    {
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        AircraftState aircraft = SayAgainTests.AddAirborne(engine, First);
        Assert.True(engine.SendCommand(First, "FH 270").Success);
        Assert.Single(Drain(engine, 10));

        Assert.True(engine.SendCommand("", "AGAINU CS").Success);

        Assert.True(aircraft.PendingRepeatCallsignOnly);
        PilotTransmission reply = Assert.Single(Drain(engine, 30));
        Assert.Equal(First, reply.Text);
        Assert.Equal("november one two three alpha bravo", reply.SpeechText);
        Assert.Equal(PilotTransmissionKind.SayReadback, reply.Kind);

        // The same line the addressed AGAIN CS produces.
        Assert.True(engine.SendCommand(First, "AGAIN CS").Success);
        PilotTransmission addressed = Assert.Single(Drain(engine, 50));
        Assert.Equal(reply.Text, addressed.Text);
        Assert.Equal(reply.SpeechText, addressed.SpeechText);
    }

    [Theory]
    [InlineData("AGAINU", "Say again → N123AB")]
    [InlineData("AGAINU CS", "Say again callsign → N123AB")]
    public void Unaddressed_ResultNamesTheChosenCallsign(string command, string expected)
    {
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        SayAgainTests.AddAirborne(engine, First);
        Assert.True(engine.SendCommand(First, "FH 270").Success);
        Assert.Single(Drain(engine, 10));

        CommandResult result = engine.SendCommand("", command);

        Assert.True(result.Success, result.Message);
        Assert.Equal(expected, result.Message);
    }

    [Fact]
    public void Unaddressed_SecondWhilePending_KeepsTheNoOpTextAndNamesTheCallsign()
    {
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        SayAgainTests.AddAirborne(engine, First);
        Assert.True(engine.SendCommand(First, "FH 270").Success);
        Assert.Single(Drain(engine, 10));
        Assert.Equal("Say again → N123AB", engine.SendCommand("", "AGAINU").Message);

        CommandResult second = engine.SendCommand("", "AGAINU");

        Assert.True(second.Success, second.Message);
        Assert.Equal("Say again already requested (N123AB)", second.Message);
    }

    [Fact]
    public void Unaddressed_RecordsTheControllerTransmission_LikeTheAddressedForm()
    {
        double addressed = DeadAirTakenBy(engine => engine.SendCommand(First, "AGAIN"));
        double unaddressed = DeadAirTakenBy(engine => engine.SendCommand("", "AGAINU"));

        Assert.True(addressed > 0, "the addressed say again should count on the frequency");
        Assert.Equal(addressed, unaddressed);
    }

    /// <summary>
    /// How much of the frequency's dead-air share <paramref name="send"/> takes at t=15, on a frequency whose one pilot read
    /// back at t=10: the controller's transmission, recorded by the post-dispatch path.
    /// </summary>
    private static double DeadAirTakenBy(Func<SimulationEngine, CommandResult> send)
    {
        SimulationEngine engine = SayAgainTests.NewEngine(soloTrainingMode: true);
        SayAgainTests.AddAirborne(engine, First);
        Assert.True(engine.SendCommand(First, "FH 270").Success);
        Assert.Single(Drain(engine, 10));
        // Clear of the read-back's airtime, so the controller's transmission is not hidden inside it.
        engine.Scenario!.ElapsedSeconds = 15;
        double before = engine.World.ActiveFrequency.ActivityMeter.DeadAirShare(20);

        CommandResult result = send(engine);

        Assert.True(result.Success, result.Message);
        double after = engine.World.ActiveFrequency.ActivityMeter.DeadAirShare(20);
        return before - after;
    }

    internal static AircraftLastPilotTransmission LastLine(AircraftState aircraft, string text, double atSeconds) =>
        new()
        {
            Text = text,
            SpeechText = $"{text}, {aircraft.Callsign}",
            Kind = PilotTransmissionKind.Readback,
            SourceKind = PilotResponder.SourceResponse,
            CanonicalAirtimeSeconds = FrequencyState.EstimateAirtimeSeconds(text),
            AtSeconds = atSeconds,
        };

    private static List<PilotTransmission> Drain(SimulationEngine engine, double elapsedSeconds) =>
        engine.World.DrainReadyPilotTransmissions(elapsedSeconds, rngSeed: 1);

    internal static void QueueProactive(AircraftState aircraft, string spoken) =>
        aircraft.PendingPilotTransmissions.Add(
            new PilotTransmission(
                aircraft.Callsign,
                spoken,
                spoken,
                PilotResponder.SourceResponse,
                PilotTransmissionKind.Proactive,
                FrequencyState.EstimateAirtimeSeconds(spoken)
            )
        );
}
