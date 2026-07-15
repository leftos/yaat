using Xunit;
using Yaat.Sim;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>
/// E2E-style unit tests for GitHub issue #297: when a pending-request reminder re-queues a
/// pilot's unanswered proactive call, the terminal SAY transcript must NOT spell the callsign
/// phonetically inline (the SAY column already carries the callsign). Both the terminal (SAY)
/// and spoken (TTS) forms of the follow-up must match the original transmission.
///
/// No recording: the defect is reproducible directly from the pending-request plumbing.
/// The original proactive call is queued from the callsign-free <c>PilotSpeechText.Terminal</c>;
/// the follow-up must re-queue that same terminal form, not the phonetic TTS string.
/// </summary>
public sealed class Issue297FollowUpCallsignLeakTests
{
    [Fact]
    public void ReadyToTaxiFollowUp_TerminalForm_IsCallsignFreeCompact()
    {
        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KAPC",
                FlightRules = "VFR",
            },
        };
        aircraft.Ground.ParkingSpot = "K1";

        var line = PilotResponder.BuildReadyToTaxi(aircraft, "ground", "A");
        // line.Terminal = "ground, at k1, with information Alpha, VFR to <dest>, ready to taxi." (callsign-free)
        // line.Tts      = "ground, november one two three alpha bravo at k1, ..., ready to taxi." (callsign spelled inline)

        // Mirror the six RecordRequest call sites: they store the full PilotSpeechText (both forms).
        PilotRequestTracker.RecordRequest(aircraft, PilotPendingRequestKind.Taxi, 0, line, PilotRequestContext.Facility("ground"));

        var queued = PilotRequestTracker.TryQueueFollowUp(aircraft, PilotRequestTracker.NormalFollowUpDelaySeconds);
        Assert.True(queued);
        var followUp = Assert.Single(aircraft.PendingPilotTransmissions);

        // The reported bug: the follow-up terminal SAY text must be the callsign-free terminal form,
        // not the phonetic TTS string.
        Assert.Equal(line.Terminal, followUp.Text);
        // Guard against shifting the defect to audio: the spoken form must still carry the callsign.
        Assert.Equal(line.Tts, followUp.SpeechText);
        // The SAY column carries the callsign separately.
        Assert.Equal("N123AB", followUp.Callsign);
    }
}
