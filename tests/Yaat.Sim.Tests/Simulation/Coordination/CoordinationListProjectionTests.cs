using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Coordination;
using Yaat.Sim.Tests.ControllerAi;

namespace Yaat.Sim.Tests.Simulation.Coordination;

/// <summary>
/// The read-only projection of the engine's STARS coordination lists that the training hub carries to YAAT clients:
/// channel order kept, items by sequence, every status included, the auto-acknowledge flag, the aircraft fields CRC
/// fills (type, beacon, category) and the sim-elapsed expiry. Real ZOA data and a real engine, as in
/// <see cref="CoordinationStepTests"/>.
/// </summary>
public class CoordinationListProjectionTests
{
    private const string ListId = "POAK";
    private const string CallsignA = "SWA1234";

    private const string OneAtOak = """
        {
          "id": "coordination-projection",
          "name": "One departure at OAK",
          "artccId": "ZOA",
          "primaryAirportId": "OAK",
          "aircraft": [
            {
              "id": "a1",
              "aircraftId": "SWA1234",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 37.7213, "lon": -122.2208 },
                "altitude": 9,
                "heading": 290,
                "speed": 0
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KOAK",
                "destination": "KSFO",
                "cruiseAltitude": 35000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 0,
              "airportId": "OAK",
              "difficulty": "Easy"
            }
          ]
        }
        """;

    /// <summary>ZOA's OAK_TWR, whose STARS TCP is 3O.</summary>
    private const string OakTwrPositionId = "01GEAMB98RKCPP9HCNPW5AVDA5";

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public CoordinationListProjectionTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        SimulationEngine engine = AiTestFixture.Load(OneAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
        scenario.StudentPosition = _zoa.ResolvePosition(OakTwrPositionId);
        scenario.StudentTcp = _zoa.GetTcpForPosition(OakTwrPositionId);
        engine.InitializeFromArtcc();
        Assert.True(scenario.CoordinationChannels.Count >= 2, "the ZOA tree should carry at least two coordination channels");
        return engine;
    }

    private static List<CoordinationChannel> Channels(SimulationEngine engine) => [.. engine.Scenario!.CoordinationChannels.Values];

    private static IReadOnlyList<CoordinationListView> Project(SimulationEngine engine, IReadOnlyList<CoordinationChannel> channels) =>
        CoordinationListProjection.Build(channels, engine.FindAircraft);

    private static CoordinationItem AddItem(CoordinationChannel channel, string callsign, StarsCoordinationStatus status, int sequence)
    {
        var item = new CoordinationItem
        {
            Id = $"{channel.ListId}-{sequence}",
            AircraftId = callsign,
            Status = status,
            OriginTcp = channel.SendingTcps[0],
            SequenceNumber = sequence,
        };
        channel.Items.Add(item);
        return item;
    }

    private static CoordinationListView ListOf(IReadOnlyList<CoordinationListView> lists, string listId) =>
        Assert.Single(lists, l => l.ListId == listId);

    [Fact]
    public void Build_KeepsTheChannelOrder_AndSortsItemsBySequence()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        List<CoordinationChannel> channels = Channels(engine);
        CoordinationChannel poak = engine.Scenario!.CoordinationChannels[ListId];
        AddItem(poak, CallsignA, StarsCoordinationStatus.Unacknowledged, 3);
        AddItem(poak, "SWA0001", StarsCoordinationStatus.Unacknowledged, 1);
        AddItem(poak, "SWA0002", StarsCoordinationStatus.Unacknowledged, 2);

        IReadOnlyList<CoordinationListView> lists = Project(engine, channels);
        List<CoordinationChannel> reversed = [.. Enumerable.Reverse(channels)];
        IReadOnlyList<CoordinationListView> reversedLists = Project(engine, reversed);

        Assert.Equal(channels.Select(c => c.ListId), lists.Select(l => l.ListId));
        Assert.Equal(reversed.Select(c => c.ListId), reversedLists.Select(l => l.ListId));
        Assert.Equal(["POAK-1", "POAK-2", "POAK-3"], ListOf(lists, ListId).Items.Select(i => i.Id));
        Assert.Equal(poak.Title, ListOf(lists, ListId).Title);
        Assert.Equal(poak.SendingTcps.Select(t => t.ToString()), ListOf(lists, ListId).SenderTcps);
        Assert.Equal(poak.Receivers.Select(r => r.Tcp.ToString()), ListOf(lists, ListId).ReceiverTcps);
    }

    [Fact]
    public void Build_IncludesAnUnsentItem()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AddItem(engine.Scenario!.CoordinationChannels[ListId], CallsignA, StarsCoordinationStatus.Unsent, 1);

        CoordinationItemView item = Assert.Single(ListOf(Project(engine, Channels(engine)), ListId).Items);

        Assert.Equal(StarsCoordinationStatus.Unsent, item.Status);
        Assert.Equal(CallsignA, item.Callsign);
    }

    [Fact]
    public void Build_AutoAcknowledge_IsTrueWhenAnyReceiverAutoAcknowledges()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        CoordinationChannel poak = engine.Scenario!.CoordinationChannels[ListId];
        Assert.NotEmpty(poak.Receivers);
        foreach (CoordinationReceiver receiver in poak.Receivers)
        {
            receiver.AutoAcknowledge = false;
        }

        Assert.False(ListOf(Project(engine, Channels(engine)), ListId).AutoAcknowledge);

        poak.Receivers[^1].AutoAcknowledge = true;

        Assert.True(ListOf(Project(engine, Channels(engine)), ListId).AutoAcknowledge);
    }

    [Fact]
    public void Build_FillsTypeBeaconAndCategory_FromTheEnginesAircraft()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState aircraft = engine.FindAircraft(CallsignA)!;
        aircraft.Transponder.AssignedCode = 425;
        AddItem(engine.Scenario!.CoordinationChannels[ListId], CallsignA, StarsCoordinationStatus.Unacknowledged, 1);

        CoordinationItemView item = Assert.Single(ListOf(Project(engine, Channels(engine)), ListId).Items);

        Assert.Equal("B738", item.AircraftType);
        Assert.Equal("0425", item.Beacon);
        Assert.Equal("F", item.Category);
    }

    [Fact]
    public void Build_AnItemWithNoAircraftInTheEngine_HasEmptyAircraftFields()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AddItem(engine.Scenario!.CoordinationChannels[ListId], "NOPE999", StarsCoordinationStatus.Unacknowledged, 1);

        CoordinationItemView item = Assert.Single(ListOf(Project(engine, Channels(engine)), ListId).Items);

        Assert.Equal("", item.AircraftType);
        Assert.Equal("", item.Beacon);
        Assert.Equal("", item.Category);
    }

    [Fact]
    public void Build_Expiry_IsTheItemsSimElapsedExpireTime()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        CoordinationChannel poak = engine.Scenario!.CoordinationChannels[ListId];
        AddItem(poak, CallsignA, StarsCoordinationStatus.Acknowledged, 1).ExpireTime = 123.5;
        AddItem(poak, "SWA0002", StarsCoordinationStatus.Unacknowledged, 2);

        CoordinationListView list = ListOf(Project(engine, Channels(engine)), ListId);

        Assert.Equal(123.5, list.Items[0].ExpireElapsedSeconds);
        Assert.Null(list.Items[1].ExpireElapsedSeconds);
    }
}
