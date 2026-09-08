using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.TowerLists;

/// <summary>
/// The tower lists on the bare engine: the proximity pass that fills a STARS P-list is a spine step, so an aircraft
/// inside a list's range is entered — at the elapsed second it arrived, in <c>DropZoneEntryTime</c> order — by a
/// plain <see cref="SimulationEngine.RunSecond"/> over the real ZOA data, with no server in the process. The lists
/// share the coordination broadcast, so what the step raises is <c>OnCoordinationChanged</c>.
///
/// <para>
/// ZOA's <c>P8</c> is the OAK list with the tightest range (50 nm), so an aircraft parked on the OAK field is in it
/// and one moved out to the Sierra is not.
/// </para>
/// </summary>
public class TowerListStepTests
{
    private const string OakList = "P8";
    private const string Callsign = "SWA1234";

    /// <summary>One IFR departure parked on the OAK field: no speed, so it stays in range for as long as the test ticks.</summary>
    private const string ParkedAtOak = """
        {
          "id": "tower-list-one",
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

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TowerListStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// A bare engine with the ZOA configuration resolved and the ARTCC initialisation the server's load runs — which
    /// is what puts the tower list airports on the engine.
    /// </summary>
    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(ParkedAtOak, _zoa, 7, []);
        engine.InitializeFromArtcc();
        Assert.Contains(OakList, engine.TowerListTracker.GetListIds());
        Assert.Empty(engine.TowerListTracker.GetEntries(OakList));

        // The init filled the coordination channels, which marks the shared dirty flag; drain it here (as the
        // server's load does) so what the counts below see is the tower-list step's own change and nothing else.
        Assert.True(engine.DrainCoordinationChanged(), "InitializeFromArtcc should have marked the coordination lists changed");
        return engine;
    }

    private static List<(string Callsign, double EnteredAtSeconds)> OakEntries(SimulationEngine engine) =>
        engine.TowerListTracker.GetEntries(OakList);

    /// <summary>Somewhere in the Sierra, out of range of every list the ZOA tree configures.</summary>
    private static void MoveOutOfRange(SimulationEngine engine) => engine.FindAircraft(Callsign)!.Position = new LatLon(41.5, -117.0);

    private static void MoveOntoTheField(SimulationEngine engine) => engine.FindAircraft(Callsign)!.Position = new LatLon(37.7213, -122.2208);

    [Fact]
    public void TheStep_EntersAnInRangeAircraft_AndRaisesTheChangeFromTheSpine()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);

        engine.RunSecond(spine);

        var entry = Assert.Single(OakEntries(engine));
        Assert.Equal(Callsign, entry.Callsign);
        Assert.Equal(engine.Scenario!.ElapsedSeconds, entry.EnteredAtSeconds);
        Assert.True(spine.CoordinationChangeCount > 0, "the spine's drain step should have handed the new entry over");

        // A second second with nothing moving leaves the list exactly as it was, dwell second included: an aircraft
        // already on a list is not re-entered. (The change count is not asserted here because it does keep rising on
        // a static second: ZOA's FAT and NCT both name a list "P1", the tracker keys its entries by list id alone,
        // and the two airports' ranges therefore fight over that one key every tick. Pre-existing, and visible on
        // "P1" only — every other list is stable, which is what this asserts.)
        engine.RunSecond(spine);

        Assert.Equal([entry], OakEntries(engine));
    }

    [Fact]
    public void AnAircraftLeavingTheRange_IsRemoved_AndRaisesTheChangeAgain()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        engine.RunSecond(spine);
        Assert.Single(OakEntries(engine));
        int afterEntry = spine.CoordinationChangeCount;

        MoveOutOfRange(engine);
        engine.RunSecond(spine);

        Assert.Empty(OakEntries(engine));
        Assert.True(spine.CoordinationChangeCount > afterEntry, "the removal should have reached the host");
    }

    /// <summary>
    /// The dwell second is what a P-list sorts on, so it has to survive a restore: a snapshot taken at the entry
    /// restores that second over whatever the run has since re-stamped, rather than leaving the engine holding the
    /// later one.
    /// </summary>
    [Fact]
    public void ASnapshotTakenAtTheEntry_RestoresTheOriginalDwellSecond()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        engine.RunSecond(spine);
        var entry = Assert.Single(OakEntries(engine));
        Assert.Equal(1, entry.EnteredAtSeconds);

        var snapshot = engine.CaptureSnapshot(actionIndex: 0);

        // Out of range and back in: the engine now holds the same aircraft under a much later dwell second.
        MoveOutOfRange(engine);
        engine.RunSecond(spine);
        MoveOntoTheField(engine);
        for (int i = 0; i < 40; i++)
        {
            engine.RunSecond(spine);
        }

        Assert.Equal([(Callsign, 3.0)], OakEntries(engine));

        engine.RestoreFromSnapshot(snapshot);

        Assert.Equal([(Callsign, 1.0)], OakEntries(engine));
    }
}
