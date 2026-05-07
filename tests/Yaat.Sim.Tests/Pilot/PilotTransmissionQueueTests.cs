using Xunit;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

public sealed class PilotTransmissionQueueTests
{
    [Fact]
    public void QueueSoloPilotTransmission_SeparatesTerminalTextFromSpeechText()
    {
        var aircraft = NewAircraft("N123AB");

        PilotResponder.QueueSoloPilotTransmission(
            aircraft,
            "[N123AB] tower, november one two three alpha bravo ten-mile final runway two eight right, with information Alpha."
        );

        Assert.Equal("tower, N123AB 10-mile final runway 28R, with information Alpha.", Assert.Single(aircraft.PendingNotifications));
        var tx = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.Equal("N123AB", tx.Callsign);
        Assert.Equal("[N123AB] tower, november one two three alpha bravo ten-mile final runway two eight right, with information Alpha.", tx.Text);
        Assert.Equal("tower, november one two three alpha bravo ten mile final runway two eight right, with information Alpha.", tx.SpeechText);
        Assert.Equal(PilotResponder.SourceResponse, tx.SourceKind);
    }

    [Fact]
    public void QueueSoloPilotTransmission_NormalizesHyphenatedTtsTokens()
    {
        var aircraft = NewAircraft("N9SX");

        PilotResponder.QueueSoloPilotTransmission(
            aircraft,
            "[N9SX] tower, november nine sierra x-ray ten-mile final, cleared touch-and-go runway two eight right."
        );

        var tx = Assert.Single(aircraft.PendingPilotTransmissions);
        Assert.Equal("tower, november nine sierra xray ten mile final, cleared touch and go runway two eight right.", tx.SpeechText);
        Assert.DoesNotContain("-ray", tx.SpeechText);
        Assert.DoesNotContain("-mile", tx.SpeechText);
        Assert.DoesNotContain("-and-", tx.SpeechText);
    }

    [Fact]
    public void DrainAllPilotTransmissions_ClearsTypedQueueOnly()
    {
        var world = new SimulationWorld();
        var aircraft = NewAircraft("SWA123");
        world.AddAircraft(aircraft);
        PilotResponder.QueueSoloPilotReadback(aircraft, "Traffic in sight");

        var drained = world.DrainAllPilotTransmissions();

        var tx = Assert.Single(drained);
        Assert.Equal("SWA123", tx.Callsign);
        Assert.Equal("Traffic in sight", tx.Text);
        Assert.Equal("Traffic in sight", tx.SpeechText);
        Assert.Empty(aircraft.PendingPilotTransmissions);
        Assert.Equal("Traffic in sight", Assert.Single(aircraft.PendingPilotReadbacks));
    }

    private static AircraftState NewAircraft(string callsign) => new() { Callsign = callsign, AircraftType = "C172" };
}
