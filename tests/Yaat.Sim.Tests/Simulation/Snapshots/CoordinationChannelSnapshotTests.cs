using Xunit;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

public class CoordinationChannelSnapshotTests
{
    [Fact]
    public void CoordinationChannels_RoundTrip_PreservesItemsAndSequence()
    {
        var tcp = new Tcp(1, "SFO_TWR", "SFO_TWR", null);
        var channel = new CoordinationChannel
        {
            Id = "ch1",
            ListId = "list1",
            Title = "Test",
            SendingTcps = [tcp],
            Receivers = [new CoordinationReceiver(tcp, true)],
            NextSequence = 3,
        };
        channel.Items.Add(
            new CoordinationItem
            {
                Id = "item1",
                AircraftId = "AAL100",
                Status = StarsCoordinationStatus.Unsent,
                Message = "test",
                OriginTcp = tcp,
                ExitFix = "FIX1",
                SequenceNumber = 2,
            }
        );

        var scenario = new SimScenarioState
        {
            ScenarioId = "s1",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
            CoordinationChannels = { ["ch1"] = channel },
        };

        var dto = scenario.ToSnapshot();
        Assert.NotNull(dto.CoordinationChannels);
        Assert.Single(dto.CoordinationChannels!);

        var restored = new SimScenarioState
        {
            ScenarioId = "s1",
            ScenarioName = "Test",
            RngSeed = 1,
            OriginalScenarioJson = "{}",
        };
        CoordinationChannelSnapshotMapper.RestoreChannels(restored.CoordinationChannels, dto.CoordinationChannels);

        var roundTripped = restored.CoordinationChannels["ch1"];
        Assert.Equal(3, roundTripped.NextSequence);
        Assert.Single(roundTripped.Items);
        Assert.Equal("AAL100", roundTripped.Items[0].AircraftId);
        Assert.True(roundTripped.Receivers[0].AutoAcknowledge);
    }
}
