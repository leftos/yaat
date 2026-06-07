using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests.PhaseTests;

/// <summary>
/// HoldingAfterExitPhase fires the pilot's "clear of runway at TAXIWAY" transmission.
/// In solo TWR mode the student should hear the radio call (TTS) so they can hand off
/// to ground; in solo GND mode the student is on a different frequency and only sees
/// a terminal warning.
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
        var ac = MakeAircraft();
        var phase = new HoldingAfterExitPhase("28R", "E", holdShortNodeId: null);

        phase.OnStart(MakeContext(ac, "TWR"));

        Assert.Empty(ac.PendingNotifications);
        var transmission = Assert.Single(ac.PendingPilotTransmissions);
        Assert.Equal("clear of runway 28R at E.", transmission.Text);
        Assert.Contains("clear of runway two eight right", transmission.SpeechText);
        Assert.Empty(ac.PendingWarnings);
    }

    [Fact]
    public void OnStart_SoloGroundStudent_TerminalWarningOnly_NoTts()
    {
        var ac = MakeAircraft();
        var phase = new HoldingAfterExitPhase("28R", "E", holdShortNodeId: null);

        phase.OnStart(MakeContext(ac, "GND"));

        // Ground student isn't on tower frequency when this transmission would fire.
        Assert.Empty(ac.PendingNotifications);
        Assert.Empty(ac.PendingPilotTransmissions);
        var warning = Assert.Single(ac.PendingWarnings);
        Assert.Contains("clear of runway 28R at E", warning);
    }

    [Fact]
    public void OnStart_RpoMode_NoTtsByDefault()
    {
        var ac = MakeAircraft();
        var phase = new HoldingAfterExitPhase("28R", "E", holdShortNodeId: null);
        var ctx = new PhaseContext
        {
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
