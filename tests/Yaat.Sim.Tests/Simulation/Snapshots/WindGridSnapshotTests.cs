using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Tests.Weather;
using Yaat.Sim.Weather;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// A snapshot carries the GFS grid by its hash inside the weather JSON and the aircraft's declination cache position, so
/// a restore flies on the same grid and the same declination as the run it was taken from.
/// </summary>
[Collection(WindGridRegistryCollection.Name)]
public sealed class WindGridSnapshotTests
{
    public WindGridSnapshotTests()
    {
        TestVnasData.EnsureInitialized();
        WindGridRegistry.ResetForTests();
    }

    [Fact]
    public void Snapshot_WithGridHash_RestoresTheGrid()
    {
        string hash = WindGridRegistry.Register(Grib2ReaderTests.LoadSample());
        StateSnapshotDto snapshot = CaptureWithWeather(WindGridRegistryTests.LiveWeatherJson(hash));

        SimulationEngine restored = LoadedEngine();
        restored.RestoreFromSnapshot(snapshot);

        Assert.Equal(hash, restored.World.Weather!.WindGridHash);
        Assert.True(WindGridRegistry.TryGetField(hash, out GriddedWindField? field));
        Assert.Same(field, restored.World.Weather.WindGrid);
    }

    [Fact]
    public void Snapshot_GridHashMissingFromRegistry_RestoresOnTheLayers()
    {
        string hash = WindGridRegistry.Register(Grib2ReaderTests.LoadSample());
        StateSnapshotDto snapshot = CaptureWithWeather(WindGridRegistryTests.LiveWeatherJson(hash));
        WindGridRegistry.ResetForTests();

        SimulationEngine restored = LoadedEngine();
        restored.RestoreFromSnapshot(snapshot);
        restored.TickOneSecond();

        WeatherProfile weather = Assert.IsType<WeatherProfile>(restored.World.Weather);
        Assert.Equal(hash, weather.WindGridHash);
        Assert.Null(weather.WindGrid);
        Assert.Equal(2, weather.WindLayers.Count);
    }

    [Fact]
    public void Snapshot_DeclinationCachePosition_RoundTrips()
    {
        SimulationEngine engine = LoadedEngine();
        engine.TickOneSecond();
        AircraftState aircraft = Assert.Single(engine.World.GetSnapshot());
        LatLon cached = Assert.IsType<LatLon>(aircraft.DeclinationCachePosition);

        var restored = AircraftState.FromSnapshot(RoundTrip(aircraft.ToSnapshot()), null);

        Assert.Equal(cached, restored.DeclinationCachePosition);

        aircraft.DeclinationCachePosition = null;
        string coldJson = JsonSerializer.Serialize(aircraft.ToSnapshot(), RecordingJsonOptions.Default);
        Assert.DoesNotContain(nameof(AircraftSnapshotDto.DeclinationCachePosition), coldJson, StringComparison.Ordinal);
        Assert.Null(AircraftState.FromSnapshot(RoundTrip(aircraft.ToSnapshot()), null).DeclinationCachePosition);
    }

    private static SimulationEngine LoadedEngine()
    {
        var engine = new SimulationEngine(new TestAirportGroundData(), PushPlanStore.CreateShipped());
        engine.LoadScenario(WindGridRegistryTests.AirborneNearOakScenarioJson, 1, MagneticDeclination.EvaluationDateUtc);
        return engine;
    }

    private static StateSnapshotDto CaptureWithWeather(string weatherJson)
    {
        SimulationEngine engine = LoadedEngine();
        engine.World.Weather = WindGridRegistryTests.ParseProfile(weatherJson);
        Assert.NotNull(engine.World.Weather.WindGrid);
        return RoundTrip(engine.CaptureSnapshot());
    }

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, RecordingJsonOptions.Default), RecordingJsonOptions.Default)!;
}
