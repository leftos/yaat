using System.IO.Compression;
using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Oracle;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Weather;

namespace Yaat.Sim.Tests.Weather;

/// <summary>
/// A session that loads GFS-grid weather mid-run replays exactly from its archive: the grid travels as an archive entry
/// named by its hash, and the replay resolves the hash in the recorded weather change to the same grid.
/// </summary>
[Collection(WindGridRegistryCollection.Name)]
public sealed class GridWeatherReplayTests
{
    private const int ChangeAtSecond = 5;
    private const int EndSecond = 20;

    private const string FdWeatherJson = """
        {
          "id": "live", "artccId": "ZOA", "name": "Live Weather (11:55Z)", "metars": ["KOAK 141153Z 29012KT 10SM FEW015 18/11 A2992"],
          "windLayers": [{ "altitude": 0, "direction": 290, "speed": 12 }, { "altitude": 12000, "direction": 270, "speed": 40 }]
        }
        """;

    public GridWeatherReplayTests()
    {
        TestVnasData.EnsureInitialized();
        WindGridRegistry.ResetForTests();
    }

    [Fact]
    public void GridWeatherReplay_MatchesTheLiveRun()
    {
        string hash = WindGridRegistry.Register(Grib2ReaderTests.LoadSample());
        string gridWeatherJson = WindGridRegistryTests.LiveWeatherJson(hash);

        var live = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        live.Replay(Recording([], 0), 0);
        var liveSnapshots = new List<StateSnapshotDto>();
        for (int t = 1; t <= EndSecond; t++)
        {
            live.TickOneSecond();
            if (t == ChangeAtSecond)
            {
                LoadWeatherLive(live, gridWeatherJson);
            }

            liveSnapshots.Add(live.CaptureSnapshot());
        }

        Assert.NotNull(live.World.Weather!.WindGrid);
        AircraftState aircraft = Assert.Single(live.World.GetSnapshot());
        Assert.NotNull(live.World.Weather.WindGrid.SampleColumn(aircraft.Position.Lat, aircraft.Position.Lon));

        byte[] archiveBytes = RecordingArchiveWriter.WriteToBytes(
            Recording([new RecordedWeatherChange(ChangeAtSecond, gridWeatherJson, false)], EndSecond)
        );
        WindGridRegistry.ResetForTests();
        using var archive = RecordingArchive.Open(new MemoryStream(archiveBytes));
        SessionRecording loaded = archive.ToBaseSessionRecording();

        var replay = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        replay.Replay(loaded, 0);
        for (int t = 1; t <= EndSecond; t++)
        {
            replay.ReplayOneSecond();
            IReadOnlyList<SnapshotDivergence> divergences = SnapshotTreeDiff.Compare(liveSnapshots[t - 1], replay.CaptureSnapshot());
            Assert.True(divergences.Count == 0, $"t={t}: {string.Join("; ", divergences.Take(5))}");
        }

        Assert.True(WindGridRegistry.TryGetField(hash, out GriddedWindField? field));
        Assert.Same(field, replay.World.Weather!.WindGrid);
    }

    /// <summary>What a live room's weather load does to the engine between two seconds, as the recorded change replays it.</summary>
    private static void LoadWeatherLive(SimulationEngine engine, string weatherJson)
    {
        SimScenarioState scenario = engine.Scenario!;
        scenario.WeatherTimeline = null;
        scenario.WeatherSourceJson = weatherJson;
        scenario.MetarReissuanceEnabled = false;
        engine.World.Weather = WindGridRegistryTests.ParseProfile(weatherJson);
    }

    [Fact]
    public void RecordingWithTenGrids_ReplaysEveryChangeOnItsGrid()
    {
        List<byte[]> grids = WindGridRegistryTests.DistinctGrids(10);
        var writerPins = new List<IDisposable>();
        var hashes = new List<string>();
        foreach (byte[] grid in grids)
        {
            writerPins.Add(WindGridRegistry.RegisterAndPin(grid, out string hash));
            hashes.Add(hash);
        }

        List<RecordedAction> changes =
        [
            .. Enumerable.Range(1, 9).Select(t => new RecordedWeatherChange(t, WindGridRegistryTests.LiveWeatherJson(hashes[t]), false)),
        ];
        byte[] archiveBytes = RecordingArchiveWriter.WriteToBytes(RecordingOn(WindGridRegistryTests.LiveWeatherJson(hashes[0]), changes, 10));
        writerPins.ForEach(pin => pin.Dispose());
        WindGridRegistry.ResetForTests();

        using var archive = RecordingArchive.Open(new MemoryStream(archiveBytes));
        SessionRecording loaded = archive.ToBaseSessionRecording();
        var replay = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        replay.Replay(loaded, 0);
        AssertFliesOn(replay, hashes[0], 0);
        for (int t = 1; t <= 9; t++)
        {
            replay.ReplayOneSecond();
            AssertFliesOn(replay, hashes[t], t);
        }
    }

    [Fact]
    public void ReplayingEngineGrid_SurvivesOtherRegistrationsBeforeItsChange()
    {
        List<byte[]> grids = WindGridRegistryTests.DistinctGrids(10);
        (SimulationEngine replay, SessionRecording _, string hash) = ReplayOfOneLaterGridChange(grids[0]);

        foreach (byte[] other in grids.Skip(1))
        {
            WindGridRegistry.Register(other);
        }

        for (int t = 1; t <= ChangeAtSecond; t++)
        {
            replay.ReplayOneSecond();
        }

        AssertFliesOn(replay, hash, ChangeAtSecond);
    }

    [Fact]
    public void ScenarioReload_ReleasesTheReplaysGridPins()
    {
        List<byte[]> grids = WindGridRegistryTests.DistinctGrids(10);
        (SimulationEngine replay, SessionRecording _, string hash) = ReplayOfOneLaterGridChange(grids[0]);

        replay.LoadScenario(WindGridRegistryTests.AirborneNearOakScenarioJson, 7, MagneticDeclination.EvaluationDateUtc);
        foreach (byte[] other in grids.Skip(1))
        {
            WindGridRegistry.Register(other);
        }

        Assert.Null(replay.Scenario!.WindGridPins);
        Assert.False(WindGridRegistry.TryGetBytes(hash, out _));
    }

    [Fact]
    public void ReplayingTheSameRecordingAgain_KeepsTheReplaysGridPinned()
    {
        List<byte[]> grids = WindGridRegistryTests.DistinctGrids(10);
        (SimulationEngine replay, SessionRecording recording, string hash) = ReplayOfOneLaterGridChange(grids[0]);

        foreach (byte[] other in grids.Skip(1))
        {
            WindGridRegistry.Register(other);
        }

        replay.Replay(recording, 0);
        for (int t = 1; t <= ChangeAtSecond; t++)
        {
            replay.ReplayOneSecond();
        }

        AssertFliesOn(replay, hash, ChangeAtSecond);
    }

    [Fact]
    public void CorruptScenarioEntry_ReleasesTheGridsRegisteredForTheLoad()
    {
        List<byte[]> grids = WindGridRegistryTests.DistinctGrids(10);
        byte[] archiveBytes;
        string hash;
        using (WindGridRegistry.RegisterAndPin(grids[0], out hash))
        {
            RecordedAction change = new RecordedWeatherChange(ChangeAtSecond, WindGridRegistryTests.LiveWeatherJson(hash), false);
            archiveBytes = RecordingArchiveWriter.WriteToBytes(Recording([change], EndSecond));
        }

        WindGridRegistry.ResetForTests();
        using var archive = RecordingArchive.Open(new MemoryStream(CorruptScenarioEntry(archiveBytes)));
        Assert.ThrowsAny<Exception>(() => archive.ToBaseSessionRecording());

        foreach (byte[] other in grids.Skip(1))
        {
            WindGridRegistry.Register(other);
        }

        Assert.False(WindGridRegistry.TryGetBytes(hash, out _), "the failed load left its grid pinned");
    }

    /// <summary>
    /// <paramref name="archiveBytes"/> with its <c>scenario.json.br</c> entry replaced by bytes that are not a Brotli stream, so
    /// reading the entry throws the way a truncated recording's does.
    /// </summary>
    private static byte[] CorruptScenarioEntry(byte[] archiveBytes)
    {
        using var source = new MemoryStream(archiveBytes, writable: false);
        using var sourceZip = new ZipArchive(source, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var destZip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in sourceZip.Entries)
            {
                ZipArchiveEntry dest = destZip.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using Stream destStream = dest.Open();
                if (string.Equals(entry.FullName, "scenario.json.br", StringComparison.Ordinal))
                {
                    destStream.Write(NotBrotli);
                }
                else
                {
                    using Stream sourceStream = entry.Open();
                    sourceStream.CopyTo(destStream);
                }
            }
        }

        return output.ToArray();
    }

    private static readonly byte[] NotBrotli = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>
    /// A replay at t=0 of a recording that changes to the grid <paramref name="grib2"/> at <see cref="ChangeAtSecond"/>, with
    /// the recording's own pins released, so only the replaying scenario's pins hold the grid.
    /// </summary>
    private static (SimulationEngine Replay, SessionRecording Recording, string Hash) ReplayOfOneLaterGridChange(byte[] grib2)
    {
        byte[] archiveBytes;
        string hash;
        using (WindGridRegistry.RegisterAndPin(grib2, out hash))
        {
            RecordedAction change = new RecordedWeatherChange(ChangeAtSecond, WindGridRegistryTests.LiveWeatherJson(hash), false);
            archiveBytes = RecordingArchiveWriter.WriteToBytes(Recording([change], EndSecond));
        }

        WindGridRegistry.ResetForTests();
        using var archive = RecordingArchive.Open(new MemoryStream(archiveBytes));
        SessionRecording loaded = archive.ToBaseSessionRecording();
        var replay = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        replay.Replay(loaded, 0);
        loaded.WindGridPins!.Dispose();
        Assert.NotNull(replay.Scenario!.WindGridPins);
        return (replay, loaded, hash);
    }

    private static void AssertFliesOn(SimulationEngine engine, string hash, int second)
    {
        Assert.True(WindGridRegistry.TryGetField(hash, out GriddedWindField? field), $"t={second}: grid {hash} was evicted");
        Assert.Same(field, engine.World.Weather!.WindGrid);
    }

    private static SessionRecording Recording(List<RecordedAction> actions, double totalElapsedSeconds) =>
        RecordingOn(FdWeatherJson, actions, totalElapsedSeconds);

    private static SessionRecording RecordingOn(string weatherJson, List<RecordedAction> actions, double totalElapsedSeconds) =>
        new()
        {
            Version = 4,
            ScenarioJson = WindGridRegistryTests.AirborneNearOakScenarioJson,
            RngSeed = 7,
            WeatherJson = weatherJson,
            Actions = actions,
            TotalElapsedSeconds = totalElapsedSeconds,
            ScenarioName = "Grid",
            ScenarioId = "grid",
            ArtccId = "ZOA",
            SessionStartUtc = MagneticDeclination.EvaluationDateUtc,
        };
}
