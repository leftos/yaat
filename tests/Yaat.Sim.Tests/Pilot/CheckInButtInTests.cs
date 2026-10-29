using Xunit;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// Pins the check-in butt-in (YAAT-1148 part 2): while the controller-response gate holds a first initial-contact
/// check-in, a second pilot's check-in gets one seeded 10% draw per candidate and first call, and on a win keys up three
/// seconds after the first check-in ends. Every path here is a way the draw could fire where it must not.
/// </summary>
public sealed class CheckInButtInTests
{
    private const int Seed = 42;

    /// <summary>Fourteen words at 0.25 s each: the first check-in's 3.5 s of airtime ends the butt-in clock.</summary>
    private const string CheckInText = "tower, november one zero zero alpha alpha ten mile final runway two eight right.";

    /// <summary>Twenty words at 0.25 s each: a third pilot's call keeps the frequency busy for five seconds.</summary>
    private const string LongTransmission =
        "november three zero zero charlie charlie cleared to land runway two eight right wind two seven zero at one two";

    /// <summary>Three words: a one-second read-back, short enough that the butt-in behind it lands inside the 8 s window.</summary>
    private const string ReadbackText = "cleared to land";

    /// <summary>A second scenario seed, for the draw's seed sensitivity.</summary>
    private const int OtherSeed = 43;

    [Fact]
    public void SecondCheckIn_WinningDraw_KeysThreeSecondsAfterTheFirst()
    {
        var frequency = new FrequencyState();
        double since = SpeakFirstCheckIn(frequency, "N569SX");
        int sinceSecond = (int)Math.Floor(since);
        string winner = FindCallsign(sinceSecond, wantWinner: true);
        string loser = FindCallsign(sinceSecond, wantWinner: false);
        Assert.NotEqual(winner, loser);
        frequency.Enqueue(Line(winner, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));

        Assert.Null(frequency.TryDequeueReady(since + 2.9, Seed));

        PilotTransmission? buttIn = frequency.TryDequeueReady(since + 3.0, Seed);

        Assert.Equal(winner, buttIn?.Callsign);
        Assert.Equal(winner, frequency.AwaitingControllerResponseTo);
    }

    [Fact]
    public void SecondCheckIn_LosingDraw_HoldsUntilTheTimeout()
    {
        var frequency = new FrequencyState();
        double since = SpeakFirstCheckIn(frequency, "N569SX");
        string loser = FindCallsign((int)Math.Floor(since), wantWinner: false);
        frequency.Enqueue(Line(loser, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));

        Assert.Null(frequency.TryDequeueReady(since + 3.0, Seed));
        Assert.Null(frequency.TryDequeueReady(since + 7.9, Seed));

        PilotTransmission? released = frequency.TryDequeueReady(since + 8.0, Seed);

        Assert.Equal(loser, released?.Callsign);
    }

    [Fact]
    public void NonCheckIn_Proactive_NeverButtsIn()
    {
        FrequencyState frequency = FirstCheckInHeld(out double since);
        string winner = FindCallsign((int)Math.Floor(since), wantWinner: true);
        // A ready-for-departure call: proactive, but not an initial contact, so the draw never applies to it.
        frequency.Enqueue(Line(winner, PilotTransmissionKind.Proactive, CheckInText, initialContact: false));

        Assert.Null(frequency.TryDequeueReady(since + 3.0, Seed));
        Assert.Null(frequency.TryDequeueReady(since + 7.9, Seed));
        Assert.Equal(winner, frequency.TryDequeueReady(since + 8.0, Seed)?.Callsign);
    }

    [Fact]
    public void Report_NeverButtsIn()
    {
        FrequencyState frequency = FirstCheckInHeld(out double since);
        string winner = FindCallsign((int)Math.Floor(since), wantWinner: true);
        // Reports are never check-ins, even one handed the flag.
        frequency.Enqueue(Line(winner, PilotTransmissionKind.Report, CheckInText, initialContact: true));

        Assert.Null(frequency.TryDequeueReady(since + 3.0, Seed));
        Assert.Null(frequency.TryDequeueReady(since + 7.9, Seed));
        Assert.Equal(winner, frequency.TryDequeueReady(since + 8.0, Seed)?.Callsign);
    }

    [Fact]
    public void FirstCallNotACheckIn_SecondCheckInDoesNotButtIn()
    {
        var frequency = new FrequencyState();
        frequency.Enqueue(Line("N569SX", PilotTransmissionKind.Proactive, CheckInText, initialContact: false));
        PilotTransmission? first = frequency.TryDequeueReady(elapsedSeconds: 0, rngSeed: Seed);
        Assert.NotNull(first);
        double since = FrequencyState.EstimateAirtimeSeconds(first.SpeechText);
        string winner = FindCallsign((int)Math.Floor(since), wantWinner: true);
        frequency.Enqueue(Line(winner, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));

        Assert.Null(frequency.TryDequeueReady(since + 3.0, Seed));
        Assert.Null(frequency.TryDequeueReady(since + 7.9, Seed));

        Assert.Equal(winner, frequency.TryDequeueReady(since + 8.0, Seed)?.Callsign);
    }

    [Fact]
    public void ControllerAnswersWithinThreeSeconds_SecondKeysUnderFifo()
    {
        FrequencyState frequency = FirstCheckInHeld(out double since);
        string winner = FindCallsign((int)Math.Floor(since), wantWinner: true);
        frequency.Enqueue(Line(winner, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));

        frequency.AcknowledgeControllerResponse("N569SX");

        // The answer releases the gate as it always has, so the second check-in speaks at once, well inside the 3 s.
        PilotTransmission? fifo = frequency.TryDequeueReady(since + 1.0, Seed);

        Assert.Equal(winner, fifo?.Callsign);
    }

    [Fact]
    public void PendingReadback_StillGoesFirst()
    {
        FrequencyState frequency = FirstCheckInHeld(out double since);
        string winner = FindCallsign((int)Math.Floor(since), wantWinner: true);
        frequency.Enqueue(Line(winner, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));
        PilotTransmission readback = Line("N300CC", PilotTransmissionKind.Readback, ReadbackText, initialContact: false);
        frequency.ExpectReadback([readback], elapsedSeconds: since);
        frequency.Enqueue(readback);

        PilotTransmission? first = frequency.TryDequeueReady(since + 3.0, Seed);

        Assert.Equal("N300CC", first?.Callsign);
        Assert.Equal(PilotTransmissionKind.Readback, first?.Kind);
        // The winner keys three seconds after the read-back's airtime ends, still inside the 8 s gate window.
        double readbackEnd = since + 3.0 + FrequencyState.EstimateAirtimeSeconds(readback.Text);
        Assert.Null(frequency.TryDequeueReady(readbackEnd + 2.9, Seed));
        Assert.Equal(winner, frequency.TryDequeueReady(readbackEnd + 3.0, Seed)?.Callsign);
    }

    [Fact]
    public void ButtIn_NeverKeysOverAnotherTransmission()
    {
        FrequencyState frequency = FirstCheckInHeld(out double since);
        string winner = FindCallsign((int)Math.Floor(since), wantWinner: true);
        // A third pilot's call goes out as the first check-in ends and holds the frequency for five seconds.
        frequency.Enqueue(Line("N300CC", PilotTransmissionKind.Readback, LongTransmission, initialContact: false));
        Assert.Equal("N300CC", frequency.TryDequeueReady(since, Seed)?.Callsign);
        frequency.Enqueue(Line(winner, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));

        // The call ends here, but the winner listens out three seconds of silence before keying (AIM 4-2-2.a).
        Assert.Null(frequency.TryDequeueReady(since + 5.0, Seed));
        Assert.Null(frequency.TryDequeueReady(since + 7.9, Seed));

        Assert.Equal(winner, frequency.TryDequeueReady(since + 8.0, Seed)?.Callsign);
    }

    [Fact]
    public void LateQueuedCheckIn_WinningDraw_KeysAfterThreeSecondsOfSilence()
    {
        FrequencyState frequency = FirstCheckInHeld(out double since);
        string winner = FindCallsign((int)Math.Floor(since), wantWinner: true);
        frequency.Enqueue(Line(winner, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));

        // Queued five seconds after the first check-in ended, with the frequency silent since: the three seconds of
        // silence are already past, so the winner keys into silence now rather than waiting for the 8 s ceiling.
        Assert.Equal(winner, frequency.TryDequeueReady(since + 5.0, Seed)?.Callsign);
    }

    [Fact]
    public void ButtInDraw_FollowsTheScenarioSeed()
    {
        FrequencyState frequency = FirstCheckInHeld(out double since);
        string callsign = FindSeedSensitiveCallsign((int)Math.Floor(since));
        frequency.Enqueue(Line(callsign, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));

        // Same instant, same everything but the scenario seed: the draw loses under 42 and wins under 43.
        Assert.Null(frequency.TryDequeueReady(since + 3.0, Seed));
        Assert.Equal(callsign, frequency.TryDequeueReady(since + 3.0, OtherSeed)?.Callsign);
    }

    [Fact]
    public void GateMovedToButtingPilot_AnsweringTheFirstDoesNotRelease()
    {
        FrequencyState frequency = FirstCheckInHeld(out double since);
        string winner = FindCallsign((int)Math.Floor(since), wantWinner: true);
        frequency.Enqueue(Line(winner, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));
        Assert.Equal(winner, frequency.TryDequeueReady(since + 3.0, Seed)?.Callsign);
        frequency.Enqueue(Line("N300CC", PilotTransmissionKind.Proactive, CheckInText, initialContact: false));

        // The butt-in took the gate: answering the first check-in changes nothing.
        frequency.AcknowledgeControllerResponse("N569SX");

        Assert.Equal(winner, frequency.AwaitingControllerResponseTo);
        Assert.Null(frequency.TryDequeueReady(since + 7.0, Seed));

        frequency.AcknowledgeControllerResponse(winner);
        Assert.Equal("N300CC", frequency.TryDequeueReady(since + 7.0, Seed)?.Callsign);
    }

    [Fact]
    public void SameSeed_SameResult() => Assert.Equal(DequeueProbe(), DequeueProbe());

    /// <summary>Every half second from the first check-in's end, the callsign the queue hands back (null while held).</summary>
    private static List<string?> DequeueProbe()
    {
        var frequency = new FrequencyState();
        double since = SpeakFirstCheckIn(frequency, "N569SX");
        string winner = FindCallsign((int)Math.Floor(since), wantWinner: true);
        frequency.Enqueue(Line(winner, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));
        var results = new List<string?>();
        for (double t = since; t <= since + 9.0; t += 0.5)
        {
            results.Add(frequency.TryDequeueReady(t, Seed)?.Callsign);
        }

        return results;
    }

    /// <summary>A frequency with only the first initial-contact check-in on it, held for the controller's answer.</summary>
    private static FrequencyState FirstCheckInHeld(out double since)
    {
        var frequency = new FrequencyState();
        since = SpeakFirstCheckIn(frequency, "N569SX");
        return frequency;
    }

    /// <summary>Queues and speaks the first initial-contact check-in at t=0; returns the second its airtime ends at.</summary>
    private static double SpeakFirstCheckIn(FrequencyState frequency, string callsign)
    {
        frequency.Enqueue(Line(callsign, PilotTransmissionKind.Proactive, CheckInText, initialContact: true));
        PilotTransmission? first = frequency.TryDequeueReady(elapsedSeconds: 0, rngSeed: Seed);
        Assert.NotNull(first);
        return FrequencyState.EstimateAirtimeSeconds(first.SpeechText);
    }

    /// <summary>
    /// The first callsign whose seeded butt-in draw wins at <see cref="OtherSeed"/> and loses at <see cref="Seed"/>, at
    /// <paramref name="firstCallEndSecond"/>.
    /// </summary>
    private static string FindSeedSensitiveCallsign(int firstCallEndSecond)
    {
        for (int i = 1; i <= 200; i++)
        {
            string callsign = $"N{i}";
            if (
                SpokenFormDraws.DrawCheckInButtIn(callsign, firstCallEndSecond, OtherSeed)
                && !SpokenFormDraws.DrawCheckInButtIn(callsign, firstCallEndSecond, Seed)
            )
            {
                return callsign;
            }
        }

        throw new InvalidOperationException($"No callsign from N1 to N200 wins at seed {OtherSeed} and loses at seed {Seed}.");
    }

    /// <summary>The first callsign whose seeded butt-in draw, at <paramref name="firstCallEndSecond"/>, wins or loses.</summary>
    private static string FindCallsign(int firstCallEndSecond, bool wantWinner)
    {
        for (int i = 1; i <= 200; i++)
        {
            string callsign = $"N{i}";
            if (SpokenFormDraws.DrawCheckInButtIn(callsign, firstCallEndSecond, Seed) == wantWinner)
            {
                return callsign;
            }
        }

        throw new InvalidOperationException(
            $"No callsign from N1 to N200 whose check-in butt-in draw {(wantWinner ? "wins" : "loses")} at second {firstCallEndSecond}."
        );
    }

    private static PilotTransmission Line(string callsign, PilotTransmissionKind kind, string text, bool initialContact) =>
        new(callsign, text, text, PilotResponder.SourceResponse, kind, FrequencyState.EstimateAirtimeSeconds(text))
        {
            IsInitialContact = initialContact,
        };
}
