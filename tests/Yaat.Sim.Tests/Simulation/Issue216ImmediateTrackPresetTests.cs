using Xunit;
using Yaat.Sim;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #216 — an immediate (unconditional) track-command preset must reach the track engine. A bare
/// <c>HO 2W</c> preset never enters the command queue: the leading block applies inline through
/// <see cref="Yaat.Sim.Commands.CommandDispatcher.ApplyCommand"/>, which has no track-command arm. The
/// preset dispatcher routes such compounds straight to the track engine instead.
/// </summary>
public class Issue216ImmediateTrackPresetTests
{
    private static TrackOwner Stars(string callsign, int subset, string sectorId) => TrackOwner.CreateStars(callsign, "ZOA", subset, sectorId);

    private static ResolvedAtcPosition Atc(TrackOwner owner, int subset, string sectorId) =>
        new()
        {
            Source = new ScenarioAtc { Id = owner.Callsign },
            Owner = owner,
            Tcp = new Tcp(subset, sectorId, owner.Callsign, null),
        };

    [Fact]
    public void ImmediateHandoffPreset_OwnedAircraft_InitiatesHandoffToAtcPosition()
    {
        var engine = new SimulationEngine(new TestAirportGroundData());

        var target = Stars("SFO_B_APP", 2, "W");
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = 0,
            StudentPosition = Stars("OAK_TWR", 3, "O"),
            AtcPositions = [Atc(target, 2, "W")],
        };

        var ac = new AircraftState { Callsign = "ASA1", AircraftType = "B737" };
        ac.Track.Owner = TrackOwner.CreateEram("OAK_14_CTR", "ZOA", "14");

        var loaded = new LoadedAircraft { State = ac, PresetCommands = [new PresetCommand { Command = "HO 2W", TimeOffset = 0 }] };
        engine.DispatchPresetCommands(loaded);

        Assert.NotNull(ac.Track.HandoffPeer);
        Assert.Equal("SFO_B_APP", ac.Track.HandoffPeer!.Callsign);
        Assert.Equal(2, ac.Track.HandoffPeer.Subset);
        Assert.Equal("W", ac.Track.HandoffPeer.SectorId);
    }
}
