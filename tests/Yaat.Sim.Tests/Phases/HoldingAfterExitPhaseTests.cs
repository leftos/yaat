using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Phases;

/// <summary>
/// HoldingAfterExitPhase fires the pilot's "clear of runway at TAXIWAY" transmission, routed by the position the pilot is
/// tuned to: a pilot on the student's frequency is heard as a radio call (TTS), and one tuned away from the student and from
/// every position the room staffs is a terminal warning only.
/// </summary>
public class HoldingAfterExitPhaseTests
{
    private static AircraftState MakeAircraft(string callsign = "N569SX") =>
        new()
        {
            Callsign = callsign,
            AircraftType = "C172",
            IsOnGround = true,
        };

    private static PhaseContext MakeContext(AircraftState ac, string positionType, bool solo = true) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
            SoloTrainingMode = solo,
            StudentPositionType = positionType,
        };

    [Fact]
    public void OnStart_SoloTowerStudent_QueuesDelayedSayAndTts()
    {
        AircraftState ac = MakeAircraft();
        var phase = new HoldingAfterExitPhase("28R", "E", holdShortNodeId: null, heldShortOfRunway: null);

        phase.OnStart(MakeContext(ac, "TWR"));

        Assert.Empty(ac.PendingNotifications);
        PilotTransmission transmission = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("clear of runway 28R at E.", transmission.Text);
        Assert.Contains("clear of runway two eight right", transmission.SpeechText);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void OnStart_SoloGroundStudentNoTunedPosition_QueuesSayAndTts()
    {
        AircraftState ac = MakeAircraft();
        var phase = new HoldingAfterExitPhase("28R", "E", holdShortNodeId: null, heldShortOfRunway: null);

        phase.OnStart(MakeContext(ac, "GND"));

        // No tuned position counts as on the student's frequency: the ground student hears the call.
        Assert.Empty(ac.PendingNotifications);
        PilotTransmission transmission = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("clear of runway 28R at E.", transmission.Text);
        Assert.Contains("clear of runway two eight right", transmission.SpeechText);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void OnStart_SoloTunedAwayFromTheStudent_TerminalWarningOnly_NoTts()
    {
        AircraftState ac = MakeAircraft();
        ac.TunedPosition = TrackOwner.Elsewhere;
        var phase = new HoldingAfterExitPhase("28R", "E", holdShortNodeId: null, heldShortOfRunway: null);

        phase.OnStart(MakeContext(ac, "GND"));

        // Tuned where nobody in the room answers: the student only sees the warning.
        Assert.Empty(ac.PendingNotifications);
        Assert.Empty(ac.PendingPilotTransmissions);
        string warning = Assert.Single(ac.PendingWarnings);
        Assert.Contains("clear of runway 28R at E", warning);
    }

    [Fact]
    public void OnStart_RpoMode_NoTtsByDefault()
    {
        AircraftState ac = MakeAircraft();
        var phase = new HoldingAfterExitPhase("28R", "E", holdShortNodeId: null, heldShortOfRunway: null);
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategorization.Categorize(ac.AircraftType),
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
            SoloTrainingMode = false,
            RpoShowPilotSpeech = false,
            StudentPositionType = "TWR",
        };

        phase.OnStart(ctx);

        // RPO without show-pilot-speech: terminal warning only, no TTS.
        Assert.Empty(ac.PendingNotifications);
        Assert.Empty(ac.PendingPilotTransmissions);
        Assert.Single(ac.PendingWarnings);
    }
}
