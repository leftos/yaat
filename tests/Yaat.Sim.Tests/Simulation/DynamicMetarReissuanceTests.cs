using System.Text.Json;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Covers the persistence/serialization mechanism that lets dynamic METAR re-issuance (and v2
/// weather-timeline evolution) survive a snapshot-based rewind and a recording load — the fix for
/// "dynamic METARs stop after rewind/recording load". The live issuer is rebuilt server-side; these
/// tests pin the Sim-side intent flag, the weather-source persistence, and the timeline rebuild.
/// </summary>
public class DynamicMetarReissuanceTests
{
    private const string StaticWeatherJson =
        """{"metars":["KOAK 011840Z 27012KT 10SM CLR 18/12 A2992"],"windLayers":[{"altitude":0,"direction":270,"speed":12}]}""";

    private static string TimelineJson()
    {
        var timeline = new WeatherTimeline
        {
            Name = "Test",
            Periods =
            [
                new WeatherPeriod
                {
                    StartMinutes = 0,
                    WindLayers =
                    [
                        new WindLayer
                        {
                            Altitude = 0,
                            Direction = 270,
                            Speed = 12,
                        },
                    ],
                    Metars = ["KOAK 011840Z 27012KT 10SM CLR 18/12 A2992"],
                },
            ],
        };
        return JsonSerializer.Serialize(timeline);
    }

    // --- RecordedWeatherChange.ReconstructMetars round-trip ---

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordedWeatherChange_RoundTrips_ReconstructMetars(bool reconstructMetars)
    {
        RecordedAction action = new RecordedWeatherChange(42.0, StaticWeatherJson, reconstructMetars);

        var json = JsonSerializer.Serialize(action, RecordingJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<RecordedAction>(json, RecordingJsonOptions.Default);

        var weather = Assert.IsType<RecordedWeatherChange>(restored);
        Assert.Equal(42.0, weather.ElapsedSeconds);
        Assert.Equal(StaticWeatherJson, weather.WeatherJson);
        Assert.Equal(reconstructMetars, weather.ReconstructMetars);
    }

    [Fact]
    public void RecordedWeatherChange_OldPayloadWithoutFlag_DefaultsFalse()
    {
        // A recording written before ReconstructMetars existed: the property is simply absent.
        const string legacyJson = """{"$type":"WeatherChange","ElapsedSeconds":7,"WeatherJson":"X"}""";

        var restored = JsonSerializer.Deserialize<RecordedAction>(legacyJson, RecordingJsonOptions.Default);

        var weather = Assert.IsType<RecordedWeatherChange>(restored);
        Assert.Equal(7.0, weather.ElapsedSeconds);
        Assert.False(weather.ReconstructMetars);
    }

    // --- Snapshot DTO round-trip ---

    [Fact]
    public void ScenarioSnapshot_RoundTrips_IntentAndSource()
    {
        var snapshot = BuildSnapshot(metarReissuanceEnabled: true, weatherSourceJson: TimelineJson());

        var json = JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;

        Assert.True(restored.Scenario.MetarReissuanceEnabled);
        Assert.Equal(TimelineJson(), restored.Scenario.WeatherSourceJson);
    }

    [Fact]
    public void ScenarioSnapshot_LegacyV10_MigratesToCurrent_WithDefaults()
    {
        var snapshot = BuildSnapshot(metarReissuanceEnabled: false, weatherSourceJson: null);
        snapshot.SchemaVersion = 10;

        SnapshotSchemaMigrator.Migrate(snapshot);

        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.False(snapshot.Scenario.MetarReissuanceEnabled);
        Assert.Null(snapshot.Scenario.WeatherSourceJson);
    }

    // --- RestoreFromSnapshot rebuilds the timeline + intent ---

    [Fact]
    public void RestoreFromSnapshot_TimelineSource_RebuildsTimeline_AndIntent()
    {
        var engine = NewEngineWithScenario();
        var snapshot = BuildSnapshot(metarReissuanceEnabled: true, weatherSourceJson: TimelineJson());

        engine.RestoreFromSnapshot(snapshot);

        Assert.True(engine.Scenario!.MetarReissuanceEnabled);
        Assert.NotNull(engine.Scenario.WeatherTimeline);
        Assert.Equal(TimelineJson(), engine.Scenario.WeatherSourceJson);
    }

    [Fact]
    public void RestoreFromSnapshot_StaticSource_LeavesTimelineNull()
    {
        var engine = NewEngineWithScenario();
        var snapshot = BuildSnapshot(metarReissuanceEnabled: true, weatherSourceJson: StaticWeatherJson);

        engine.RestoreFromSnapshot(snapshot);

        Assert.True(engine.Scenario!.MetarReissuanceEnabled);
        Assert.Null(engine.Scenario.WeatherTimeline);
    }

    [Fact]
    public void RestoreFromSnapshot_NoSource_ClearsTimeline()
    {
        var engine = NewEngineWithScenario();
        // Pre-seed a timeline to prove restore clears it when the snapshot has no source.
        engine.Scenario!.WeatherTimeline = WeatherTimelineParser.Parse(TimelineJson()).Timeline;

        engine.RestoreFromSnapshot(BuildSnapshot(metarReissuanceEnabled: false, weatherSourceJson: null));

        Assert.False(engine.Scenario.MetarReissuanceEnabled);
        Assert.Null(engine.Scenario.WeatherTimeline);
    }

    // --- Recording archive manifest round-trip ---

    [Fact]
    public void RecordingArchive_RoundTrips_MetarReissuanceEnabled()
    {
        var recording = new SessionRecording
        {
            Version = 4,
            ScenarioJson = """{"scenarioId":"t","name":"T"}""",
            RngSeed = 1,
            WeatherJson = StaticWeatherJson,
            MetarReissuanceEnabled = true,
            Actions = [],
            TotalElapsedSeconds = 0,
            Snapshots = [],
            ScenarioName = "T",
            ScenarioId = "t",
        };

        var bytes = RecordingArchiveWriter.WriteToBytes(recording);
        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        Assert.True(archive.Manifest.MetarReissuanceEnabled);
        Assert.True(archive.ToSessionRecording().MetarReissuanceEnabled);
    }

    [Fact]
    public void RecordingArchive_NoWeather_MetarReissuanceEnabledFalse()
    {
        var recording = new SessionRecording
        {
            Version = 4,
            ScenarioJson = """{"scenarioId":"t","name":"T"}""",
            RngSeed = 1,
            WeatherJson = null,
            MetarReissuanceEnabled = true, // ignored — no weather to re-issue
            Actions = [],
            TotalElapsedSeconds = 0,
            Snapshots = [],
            ScenarioName = "T",
            ScenarioId = "t",
        };

        var bytes = RecordingArchiveWriter.WriteToBytes(recording);
        using var ms = new MemoryStream(bytes);
        using var archive = RecordingArchive.Open(ms);

        Assert.False(archive.Manifest.MetarReissuanceEnabled);
    }

    private static SimulationEngine NewEngineWithScenario() =>
        new(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
            },
        };

    private static StateSnapshotDto BuildSnapshot(bool metarReissuanceEnabled, string? weatherSourceJson) =>
        new()
        {
            ElapsedSeconds = 0,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [],
            Scenario = new ScenarioSnapshotDto
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 42,
                ElapsedSeconds = 0,
                AutoClearedToLand = false,
                AutoCrossRunway = false,
                ValidateDctFixes = false,
                IsPaused = false,
                SimRate = 1.0,
                AutoAcceptDelaySeconds = 0,
                IsStudentTowerPosition = false,
                MetarReissuanceEnabled = metarReissuanceEnabled,
                WeatherSourceJson = weatherSourceJson,
            },
        };
}
