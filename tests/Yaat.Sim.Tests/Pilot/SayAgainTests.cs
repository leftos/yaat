using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Spine;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// The student's addressed "say again" (<c>AGAIN</c>, <c>AGAIN CS</c>): the pilot repeats its last transmission word for
/// word, or says only its callsign, once its own queued line has gone out; it answers "I didn't say anything" when it has
/// not spoken. The request is refused outside solo training and for an aircraft no longer on the student's frequency, and
/// it is invisible to every gate and tracker a controller transmission normally moves: it acknowledges no check-in, clears
/// no response gate, arms no awaited read-back, scores nothing and starts no follow-up.
/// </summary>
public sealed class SayAgainTests
{
    private const string Callsign = "N123AB";
    private const string SpokenCallsign = "november one two three alpha bravo";

    /// <summary>Forty words at 0.25 s each: ten seconds of airtime, long enough to hold a repeat in the queue.</summary>
    private const string FortyWordTransmission =
        "tower november four five six charlie delta is ten miles east at three thousand five hundred inbound for landing with "
        + "information alpha request runway two eight left and full stop and then a taxi to the north field fuel island please";

    public SayAgainTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Again_RepeatsTheLastReadbackVerbatim()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        Assert.True(engine.SendCommand(Callsign, "FH 270").Success);
        PilotTransmission original = Assert.Single(Drain(engine, 10));

        CommandResult result = engine.SendCommand(Callsign, "AGAIN");

        Assert.True(result.Success, result.Message);
        Assert.True(aircraft.PendingRepeat);
        PilotTransmission repeat = Assert.Single(Drain(engine, 30));
        Assert.True(repeat.IsRepeat);
        Assert.Equal(original.Text, repeat.Text);
        Assert.Equal(original.SpeechText, repeat.SpeechText);
        Assert.Equal(original.Kind, repeat.Kind);
        Assert.Equal(original.SourceKind, repeat.SourceKind);
        Assert.Equal(original.CanonicalAirtimeSeconds, repeat.CanonicalAirtimeSeconds);
        Assert.False(repeat.IsInitialContact);
        Assert.False(aircraft.PendingRepeat);
        // The repeat does not overwrite what the aircraft last said, nor when it said it.
        Assert.Equal(10, aircraft.LastPilotTransmission!.AtSeconds);
        Assert.Empty(Drain(engine, 60));
    }

    [Fact]
    public void Again_RepeatsAProactiveCall_AndRearmsTheResponseGate()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState first = AddAirborne(engine, Callsign);
        AircraftState second = AddAirborne(engine, "N456CD");
        QueueProactive(first, "tower, november one two three alpha bravo, ten miles south, inbound landing.");
        PilotTransmission call = Assert.Single(Drain(engine, 10));
        Assert.Equal(Callsign, engine.World.ActiveFrequency.AwaitingControllerResponseTo);

        // The other pilot's call goes out once the gate times out, and the frequency now waits on that pilot.
        QueueProactive(second, "tower, november four five six charlie delta, eight miles east, inbound landing.");
        Assert.Equal("N456CD", Assert.Single(Drain(engine, 40)).Callsign);
        Assert.Equal("N456CD", engine.World.ActiveFrequency.AwaitingControllerResponseTo);

        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        PilotTransmission repeat = Assert.Single(Drain(engine, 70));

        Assert.Equal(call.Text, repeat.Text);
        Assert.Equal(PilotTransmissionKind.Proactive, repeat.Kind);
        Assert.Equal(Callsign, engine.World.ActiveFrequency.AwaitingControllerResponseTo);
    }

    [Fact]
    public void Again_RepeatIsSpokenAndLandsOnTheTerminalAsASecondLine()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AddAirborne(engine, Callsign);
        var said = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => said.Add(entry);
        // The bare host, with every OnPilotTransmissions payload (what the server broadcasts and the client speaks) recorded.
        var host = new SpineCapturingHost(engine.BareHost);
        Assert.True(engine.SendCommand(Callsign, "FH 270").Success);
        Assert.True(TickUntil(engine, host, () => PilotLines(said).Count == 1, 10) >= 0, "the read-back should go out");

        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        Assert.True(TickUntil(engine, host, () => PilotLines(said).Count == 2, 20) >= 0, "the repeat should go out");

        List<TerminalEntry> lines = PilotLines(said);
        Assert.Equal("SayPilot", lines[1].Kind);
        Assert.Equal(lines[0].Kind, lines[1].Kind);
        Assert.Equal(lines[0].Message, lines[1].Message);
        Assert.Equal("fly heading 270", lines[1].Message);
        List<PilotTransmission> spoken = [.. host.PilotTransmissions.Where(t => (t.Callsign == Callsign) && (t.Text == "fly heading 270"))];
        Assert.Equal(2, spoken.Count);
        Assert.False(spoken[0].IsRepeat);
        Assert.True(spoken[1].IsRepeat);
        Assert.Equal(spoken[0].SpeechText, spoken[1].SpeechText);
        Assert.NotNull(spoken[1].LoadBand);
    }

    [Fact]
    public void AgainCs_RepeatsOnlyTheCallsign_SpokenForm()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        Assert.True(engine.SendCommand(Callsign, "FH 270").Success);
        PilotTransmission original = Assert.Single(Drain(engine, 10));

        Assert.True(engine.SendCommand(Callsign, "AGAIN CS").Success);
        Assert.True(aircraft.PendingRepeatCallsignOnly);
        Assert.False(aircraft.PendingRepeat);
        PilotTransmission reply = Assert.Single(Drain(engine, 30));

        Assert.Equal(Callsign, reply.Text);
        // The full callsign, never the abbreviated "three alpha bravo".
        Assert.Equal(SpokenCallsign, reply.SpeechText);
        Assert.Equal(PilotTransmissionKind.SayReadback, reply.Kind);
        Assert.False(aircraft.PendingRepeatCallsignOnly);
        Assert.Equal(original.Text, aircraft.LastPilotTransmission!.Text);
    }

    [Theory]
    [InlineData("AGAIN")]
    [InlineData("AGAIN CS")]
    public void Again_WithNothingToRepeat_AnswersIDidntSayAnything(string command)
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);

        Assert.True(engine.SendCommand(Callsign, command).Success);
        PilotTransmission reply = Assert.Single(Drain(engine, 10));

        Assert.Equal("I didn't say anything", reply.Text);
        Assert.Equal($"{SpokenCallsign}, I didn't say anything", reply.SpeechText);
        Assert.Equal(PilotTransmissionKind.SayReadback, reply.Kind);
        // The answer is not something the pilot can be asked to repeat: a second say again answers the same way.
        Assert.Null(aircraft.LastPilotTransmission);
        Assert.False(aircraft.PendingRepeat || aircraft.PendingRepeatCallsignOnly);
        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        Assert.Equal("I didn't say anything", Assert.Single(Drain(engine, 30)).Text);
    }

    [Fact]
    public void Again_WhileALineIsQueued_RepeatsBehindIt_AndRepeatsTheNewLine()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        Assert.True(engine.SendCommand(Callsign, "FH 270").Success);
        Assert.Equal("fly heading 270", Assert.Single(Drain(engine, 10)).Text);
        Assert.True(engine.SendCommand(Callsign, "FH 090").Success);

        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);

        // The queued read-back goes first, alone: the repeat waits until the pilot's own line has gone.
        PilotTransmission queued = Assert.Single(Drain(engine, 30));
        Assert.Equal("fly heading 090", queued.Text);
        Assert.False(queued.IsRepeat);
        Assert.True(aircraft.PendingRepeat);
        PilotTransmission repeat = Assert.Single(Drain(engine, 60));
        Assert.True(repeat.IsRepeat);
        Assert.Equal("fly heading 090", repeat.Text);
    }

    [Fact]
    public void Again_SecondWhilePending_IsANoOp()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        Assert.True(engine.SendCommand(Callsign, "FH 270").Success);
        PilotTransmission original = Assert.Single(Drain(engine, 10));

        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        Assert.True(engine.SendCommand(Callsign, "AGAIN CS").Success);

        // The first request stands: one whole repeat, never a callsign-only one, and nothing more after it.
        Assert.True(aircraft.PendingRepeat);
        Assert.False(aircraft.PendingRepeatCallsignOnly);
        Assert.Equal(original.Text, Assert.Single(Drain(engine, 30)).Text);
        Assert.Empty(Drain(engine, 60));
        Assert.Empty(Drain(engine, 90));
    }

    [Fact]
    public void Again_OutsideSoloTraining_IsRefused()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: false);
        AircraftState aircraft = AddAirborne(engine, Callsign);

        CommandResult result = engine.SendCommand(Callsign, "AGAIN");

        Assert.False(result.Success);
        Assert.True(result.IsOperatorGuidance);
        Assert.Contains("solo", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(aircraft.PendingRepeat);
    }

    [Fact]
    public void Again_AfterHandoff_IsRefused()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        Assert.True(engine.SendCommand(Callsign, "FH 270").Success);
        Assert.Single(Drain(engine, 10));
        // A say again already pending when the aircraft is sent away is dropped with it.
        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        Assert.True(engine.SendCommand(Callsign, "FCA").Success);
        Assert.True(aircraft.HasLeftStudentFrequency);
        List<PilotTransmission> afterHandoff = [.. Drain(engine, 30), .. Drain(engine, 60)];
        Assert.DoesNotContain(afterHandoff, t => t.IsRepeat);
        Assert.False(aircraft.PendingRepeat);

        CommandResult result = engine.SendCommand(Callsign, "AGAIN");

        Assert.False(result.Success);
        Assert.Equal($"{Callsign} is no longer on your frequency", result.Message);
        Assert.False(aircraft.PendingRepeat);
    }

    [Fact]
    public void Again_RepeatQueuedBehindAnotherPilot_IsNotSpokenAfterHandoff()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        AircraftState other = AddAirborne(engine, "N456CD");
        Assert.True(engine.SendCommand(Callsign, "FH 270").Success);
        Assert.Single(Drain(engine, 10));
        // Another pilot takes the frequency with a forty-word call, on the air until t=23.
        QueueProactive(other, FortyWordTransmission);
        Assert.Equal("N456CD", Assert.Single(Drain(engine, 13)).Callsign);

        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        // The copy is materialised behind the other pilot's call and waits there for airtime.
        Assert.Empty(Drain(engine, 14));
        Assert.True(engine.World.ActiveFrequency.HasPendingFrom(Callsign));
        Assert.True(engine.SendCommand(Callsign, "FCA").Success);

        List<PilotTransmission> afterHandoff = [.. Drain(engine, 30), .. Drain(engine, 60), .. Drain(engine, 90)];

        Assert.DoesNotContain(afterHandoff, t => (t.Callsign == Callsign) && t.IsRepeat);
        Assert.Contains(afterHandoff, t => (t.Callsign == Callsign) && (t.Kind == PilotTransmissionKind.Readback) && !t.IsRepeat);
        Assert.False(aircraft.PendingRepeat);
    }

    [Fact]
    public void Again_DoesNotAcknowledgeInitialContact()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        AircraftState control = AddAirborne(engine, "N456CD");

        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        Assert.True(engine.SendCommand("N456CD", "STBY").Success);

        Assert.False(aircraft.HasControllerAcknowledgedInitialContact);
        Assert.False(aircraft.HasMadeInitialContact);
        // A STBY to the other aircraft in the same room does acknowledge it: the difference is the verb.
        Assert.True(control.HasControllerAcknowledgedInitialContact);
    }

    [Fact]
    public void Again_DoesNotClearTheControllerResponseGate()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        QueueProactive(aircraft, "tower, november one two three alpha bravo, ten miles south, inbound landing.");
        Assert.Single(Drain(engine, 10));
        Assert.Equal(Callsign, engine.World.ActiveFrequency.AwaitingControllerResponseTo);

        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);

        Assert.Equal(Callsign, engine.World.ActiveFrequency.AwaitingControllerResponseTo);
    }

    [Fact]
    public void Again_DoesNotArmAnAwaitedReadback()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        AircraftState other = AddAirborne(engine, "N456CD");
        Assert.True(engine.SendCommand(Callsign, "FH 270").Success);
        Assert.Single(Drain(engine, 10));
        Assert.Null(engine.World.ActiveFrequency.AwaitingReadbackFrom);

        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);

        Assert.Null(engine.World.ActiveFrequency.AwaitingReadbackFrom);
        Assert.Empty(aircraft.PendingPilotTransmissions);
        // The repeated read-back holds nobody: the other pilot's call, queued first, goes out ahead of it.
        QueueProactive(other, "tower, november four five six charlie delta, eight miles east, inbound landing.");
        PilotTransmission next = Assert.Single(Drain(engine, 30));
        Assert.Equal("N456CD", next.Callsign);
        Assert.Null(engine.World.ActiveFrequency.AwaitingReadbackFrom);
    }

    [Fact]
    public void Again_RepeatCountsInTheFrequencyLoad()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AddAirborne(engine, Callsign);
        Assert.True(engine.SendCommand(Callsign, "FH 270").Success);
        PilotTransmission original = Assert.Single(Drain(engine, 10));
        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        FrequencyActivityMeter meter = engine.World.ActiveFrequency.ActivityMeter;
        double deadAirBefore = meter.DeadAirShare(35);

        PilotTransmission repeat = Assert.Single(Drain(engine, 30));

        Assert.Equal(original.CanonicalAirtimeSeconds, repeat.CanonicalAirtimeSeconds);
        Assert.True(meter.DeadAirShare(35) < deadAirBefore, "the repeat's airtime should count on the frequency");
        Assert.NotNull(repeat.LoadBand);
    }

    [Fact]
    public void Again_IsNeutralForSoloScoring()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        Assert.True(engine.SendCommand(Callsign, "FH 270").Success);
        Assert.Single(Drain(engine, 10));
        string before = SoloReport(engine, aircraft);

        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        Assert.True(engine.SendCommand(Callsign, "AGAIN CS").Success);
        Assert.Single(Drain(engine, 30));

        // The session report the student is debriefed from is unchanged, and the aircraft is still the student's.
        Assert.Equal(before, SoloReport(engine, aircraft));
        Assert.False(aircraft.HasLeftStudentFrequency);
    }

    /// <summary>The solo-training session report as the hub builds it, serialized so two reads compare field by field.</summary>
    private static string SoloReport(SimulationEngine engine, AircraftState aircraft)
    {
        double elapsed = engine.Scenario!.ElapsedSeconds;
        SoloTrainingReportData report = engine.SoloTrainingEvaluator.BuildReport(
            soloTrainingMode: true,
            elapsed,
            engine.ApproachEvaluator.BuildReport(elapsed),
            new AircraftDebriefContext([aircraft], [], null)
        );
        return JsonSerializer.Serialize(report);
    }

    [Fact]
    public void Again_DoesNotStartAFollowUpOrNewRequest()
    {
        SimulationEngine engine = NewEngine(soloTrainingMode: true);
        AircraftState aircraft = AddAirborne(engine, Callsign);
        var line = new PilotSpeechText(
            "Oakland tower, ten miles south, request landing",
            "Oakland tower, november one two three alpha bravo, ten miles south, request landing."
        );
        PilotRequestTracker.RecordRequest(aircraft, PilotPendingRequestKind.Taxi, nowSeconds: 10, line, PilotRequestContext.None);
        PilotPendingRequest request = aircraft.PendingPilotRequest!;
        double followUpDue = request.NextFollowUpDueSeconds;
        QueueProactive(aircraft, line.Tts);
        Assert.Single(Drain(engine, 10));

        Assert.True(engine.SendCommand(Callsign, "AGAIN").Success);
        Assert.True(Assert.Single(Drain(engine, 30)).IsRepeat);

        Assert.Same(request, aircraft.PendingPilotRequest);
        Assert.Equal(PilotPendingRequestResponseState.None, request.ResponseState);
        Assert.Equal(followUpDue, request.NextFollowUpDueSeconds);
        Assert.Empty(aircraft.PendingPilotTransmissions);
    }

    internal static SimulationEngine NewEngine(bool soloTrainingMode) =>
        new(new TestAirportGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "say-again",
                ScenarioName = "Say again",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
                ElapsedSeconds = 10,
                SoloTrainingMode = soloTrainingMode,
            },
        };

    /// <summary>A C172 at 3,000 ft over the East Bay south of Oakland, heading north.</summary>
    internal static AircraftState AddAirborne(SimulationEngine engine, string callsign)
    {
        var aircraft = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "C172",
            Position = new LatLon(37.62, -122.15),
            TrueHeading = new TrueHeading(360),
            Altitude = 3000,
            IndicatedAirspeed = 100,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan(),
        };
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }

    private static List<PilotTransmission> Drain(SimulationEngine engine, double elapsedSeconds) =>
        engine.World.DrainReadyPilotTransmissions(elapsedSeconds, rngSeed: 1);

    private static void QueueProactive(AircraftState aircraft, string spoken) =>
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

    /// <summary>The terminal's pilot lines carrying the heading read-back, whatever else the pilot says meanwhile.</summary>
    private static List<TerminalEntry> PilotLines(List<TerminalEntry> said) =>
        [.. said.Where(e => (e.Callsign == Callsign) && (e.Kind is "SayPilot" or "SayReadback") && (e.Message == "fly heading 270"))];

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

    /// <summary>Like <see cref="TickUntil(SimulationEngine, Func{bool}, int)"/>, running each second through <paramref name="host"/>.</summary>
    private static int TickUntil(SimulationEngine engine, ISimulationHost host, Func<bool> done, int maxSeconds)
    {
        for (int i = 1; i <= maxSeconds; i++)
        {
            engine.RunSecond(host);
            if (done())
            {
                return i;
            }
        }

        return -1;
    }
}
