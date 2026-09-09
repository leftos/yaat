using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
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
    /// <summary>NCT's <c>P8</c>: the OAK list with the tightest range (50 nm).</summary>
    private static readonly TowerListKey OakList = new("NCT", "P8");

    /// <summary>NCT's <c>P1</c> (the SJC list, 150 nm) and FAT's own <c>P1</c> (FAT, 30 nm) — one id, two facilities.</summary>
    private static readonly TowerListKey NctSharedListId = new("NCT", "P1");

    private static readonly TowerListKey FatSharedListId = new("FAT", "P1");

    private const string Callsign = "SWA1234";
    private const string FatCallsign = "AAL750";

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

    /// <summary>
    /// The same OAK departure with a Fresno arrival added, spawned three seconds later so the two carry distinct
    /// dwell seconds. FAT is 130 nm from OAK: it is on FAT's own P1 (30 nm) and — like the OAK departure — inside
    /// NCT's P1, which is the SJC list at 150 nm.
    /// </summary>
    private const string OakAndFatTraffic = """
        {
          "id": "tower-list-two-facilities",
          "name": "One departure at OAK, one arrival over FAT",
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
            },
            {
              "id": "a2",
              "aircraftId": "AAL750",
              "aircraftType": "B738",
              "transponderMode": "C",
              "startingConditions": {
                "type": "Coordinates",
                "coordinates": { "lat": 36.7762, "lon": -119.7181 },
                "altitude": 10000,
                "heading": 90,
                "speed": 250
              },
              "flightplan": {
                "rules": "IFR",
                "departure": "KLAX",
                "destination": "KFAT",
                "cruiseAltitude": 25000,
                "cruiseSpeed": 250,
                "route": "",
                "remarks": "",
                "aircraftType": "B738"
              },
              "presetCommands": [],
              "spawnDelay": 3,
              "airportId": "FAT",
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
    private SimulationEngine? Engine() => Engine(ParkedAtOak);

    private SimulationEngine? Engine(string scenarioJson)
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(scenarioJson, _zoa, 7, []);
        engine.InitializeFromArtcc();
        Assert.Contains(OakList, engine.TowerListTracker.GetLists());
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
        // already on a list is not re-entered, and no list reports a change — the two facilities that both name a
        // list "P1" hold separate buckets, so neither evicts the other on a static second.
        int afterEntry = spine.CoordinationChangeCount;
        engine.RunSecond(spine);

        Assert.Equal([entry], OakEntries(engine));
        Assert.Equal(afterEntry, spine.CoordinationChangeCount);
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

    /// <summary>
    /// FAT and NCT both declare a list called <c>P1</c>, over different airports. Each holds its own entries, and a
    /// snapshot round-trip puts each facility's list back as it was — the callsigns in their own dwell order, neither
    /// list carrying the other's traffic.
    /// </summary>
    [Fact]
    public void ASnapshotRoundTrip_RestoresEachFacilitysOwnSharedIdList()
    {
        if (Engine(OakAndFatTraffic) is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        for (int i = 0; i < 6; i++)
        {
            engine.RunSecond(spine);
        }

        var nctP1 = engine.TowerListTracker.GetEntries(NctSharedListId);
        var fatP1 = engine.TowerListTracker.GetEntries(FatSharedListId);

        // NCT's P1 is the SJC list (150 nm), so both aircraft are on it — oldest first, the OAK departure ahead of
        // the Fresno arrival that spawned three seconds later. FAT's own P1 holds the Fresno arrival alone.
        Assert.Equal([Callsign, FatCallsign], nctP1.Select(e => e.Callsign).ToList());
        Assert.True(nctP1[0].EnteredAtSeconds < nctP1[1].EnteredAtSeconds, "the OAK departure entered NCT's P1 first");
        Assert.Equal([FatCallsign], fatP1.Select(e => e.Callsign).ToList());

        var snapshot = engine.CaptureSnapshot(actionIndex: 0);

        // Both out to the Sierra: every list empties, so what the restore puts back can only have come from the snapshot.
        engine.FindAircraft(Callsign)!.Position = new LatLon(41.5, -117.0);
        engine.FindAircraft(FatCallsign)!.Position = new LatLon(41.5, -117.0);
        engine.RunSecond(spine);
        Assert.Empty(engine.TowerListTracker.GetEntries(NctSharedListId));
        Assert.Empty(engine.TowerListTracker.GetEntries(FatSharedListId));

        engine.RestoreFromSnapshot(snapshot);

        Assert.Equal(nctP1, engine.TowerListTracker.GetEntries(NctSharedListId));
        Assert.Equal(fatP1, engine.TowerListTracker.GetEntries(FatSharedListId));
    }

    /// <summary>
    /// A snapshot written before the lists were keyed per facility names a list id with no facility. It cannot be
    /// attributed to one — FAT's P1 and NCT's P1 are different lists — so the restore drops it and says so, rather
    /// than restoring the entries onto a guess. The list it would have been guessed onto is populated first, so a
    /// restore that guessed would be visible as the wrong callsign on NCT's P1 rather than as an empty list.
    /// </summary>
    [Fact]
    public void ARestoredListWithoutAFacilityId_IsSkipped_WithAWarning()
    {
        if (Engine(OakAndFatTraffic) is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        for (int i = 0; i < 6; i++)
        {
            engine.RunSecond(spine);
        }

        Assert.NotEmpty(engine.TowerListTracker.GetEntries(NctSharedListId));
        Assert.NotEmpty(engine.TowerListTracker.GetEntries(FatSharedListId));

        // Tapped for this test's context only: SimLog.InitializeForTest never touches the process-wide factory.
        using var captured = new CapturingSimLogProvider(LogLevel.Warning, capacity: 32);
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning).AddProvider(captured));
        SimLog.InitializeForTest(factory);

        TowerListSnapshotMapper.Restore(
            engine.TowerListTracker,
            new TowerListSnapshotDto
            {
                Lists = [new TowerListEntriesDto { ListId = "P1", Entries = [new TowerListEntryDto { Callsign = "ZZZ999", EnteredAtSeconds = 4 }] }],
            }
        );

        Assert.Empty(engine.TowerListTracker.GetEntries(NctSharedListId));
        Assert.Empty(engine.TowerListTracker.GetEntries(FatSharedListId));

        var warning = Assert.Single(captured.Drain().Where(r => r.Level == LogLevel.Warning).ToList());
        Assert.Contains("P1 (no facility id)", warning.Message);
    }

    /// <summary>
    /// A list whose (facility, list) the restoring room does not configure — another ARTCC's, or one whose airport
    /// this room's navigation database could not resolve — is dropped too: nothing reads entries under a key the
    /// tracker holds no airport for, and they would sit there until the next restore. The lists the room does
    /// configure restore alongside it, and the two drops share one warning line.
    /// </summary>
    [Fact]
    public void ARestoredListTheRoomDoesNotConfigure_IsSkipped_WithTheSameWarning()
    {
        if (Engine(OakAndFatTraffic) is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        for (int i = 0; i < 6; i++)
        {
            engine.RunSecond(spine);
        }

        var liveNctP1 = engine.TowerListTracker.GetEntries(NctSharedListId);
        Assert.NotEmpty(liveNctP1);

        using var captured = new CapturingSimLogProvider(LogLevel.Warning, capacity: 32);
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning).AddProvider(captured));
        SimLog.InitializeForTest(factory);

        TowerListSnapshotMapper.Restore(
            engine.TowerListTracker,
            new TowerListSnapshotDto
            {
                Lists =
                [
                    new TowerListEntriesDto
                    {
                        FacilityId = "ZLC",
                        ListId = "P1",
                        Entries = [new TowerListEntryDto { Callsign = "ZZZ999", EnteredAtSeconds = 4 }],
                    },
                    new TowerListEntriesDto { ListId = "P2", Entries = [new TowerListEntryDto { Callsign = "ZZZ998", EnteredAtSeconds = 5 }] },
                    new TowerListEntriesDto
                    {
                        FacilityId = NctSharedListId.FacilityId,
                        ListId = NctSharedListId.ListId,
                        Entries = [.. liveNctP1.Select(e => new TowerListEntryDto { Callsign = e.Callsign, EnteredAtSeconds = e.EnteredAtSeconds })],
                    },
                ],
            }
        );

        // The configured list came back; neither drop landed anywhere.
        Assert.Equal(liveNctP1, engine.TowerListTracker.GetEntries(NctSharedListId));
        Assert.Empty(engine.TowerListTracker.GetEntries(new TowerListKey("ZLC", "P1")));
        Assert.Empty(engine.TowerListTracker.GetEntries(FatSharedListId));

        var warning = Assert.Single(captured.Drain().Where(r => r.Level == LogLevel.Warning).ToList());
        Assert.Contains("2 tower list(s) in the snapshot could not be restored", warning.Message);
        Assert.Contains("ZLC/P1 (facility/list not configured)", warning.Message);
        Assert.Contains("P2 (no facility id)", warning.Message);
    }
}
