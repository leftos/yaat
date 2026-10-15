using System.IO.Compression;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Weather;

namespace Yaat.Sim.Tests.Weather;

/// <summary>
/// <see cref="RecordingArchiveWriter.WriteReferencedWindGrids"/> bundles every grid the weather text names, wherever it is
/// named, once per hash, for the writers that hold the weather and the action log but no <see cref="SessionRecording"/>.
/// </summary>
[Collection(WindGridRegistryCollection.Name)]
public sealed class RecordingArchiveWriterWindGridTests
{
    public RecordingArchiveWriterWindGridTests() => WindGridRegistry.ResetForTests();

    [Fact]
    public void WriteReferencedWindGrids_FromWeatherAndActions_WritesEachHashOnce()
    {
        List<byte[]> grids = WindGridRegistryTests.DistinctGrids(3);
        var pins = new List<IDisposable>();
        var hashes = new List<string>();
        foreach (byte[] grid in grids)
        {
            pins.Add(WindGridRegistry.RegisterAndPin(grid, out string hash));
            hashes.Add(hash);
        }

        string unregistered = WindGridRegistry.HashOf([1, 2, 3]);
        List<RecordedAction> actions =
        [
            new RecordedWeatherChange(1, WindGridRegistryTests.LiveWeatherJson(hashes[1]), false),
            new RecordedWeatherChange(2, WindGridRegistryTests.LiveWeatherJson(hashes[0]), false),
            new RecordedWeatherChange(3, null, false),
            new RecordedWeatherChange(4, WindGridRegistryTests.LiveWeatherJson(unregistered), false),
        ];
        string?[] snapshotWeather = [WindGridRegistryTests.LiveWeatherJson(hashes[2]), WindGridRegistryTests.LiveWeatherJson(hashes[1]), null];

        using var ms = new MemoryStream();
        using (var writer = new RecordingArchiveWriter(ms))
        {
            RecordingArchiveWriter.WriteReferencedWindGrids(writer, WindGridRegistryTests.LiveWeatherJson(hashes[0]), actions, snapshotWeather);
        }

        pins.ForEach(pin => pin.Dispose());
        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read);
        foreach (string hash in hashes)
        {
            Assert.Single(zip.Entries, e => e.FullName == RecordingArchive.WindGridEntryName(hash));
        }

        Assert.DoesNotContain(zip.Entries, e => e.FullName == RecordingArchive.WindGridEntryName(unregistered));
        Assert.Equal(hashes.Count, zip.Entries.Count);
    }
}
