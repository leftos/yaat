using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Yaat.Sim.Tests.Helpers;
using Yaat.Sim.Weather;

namespace Yaat.Sim.Tests.Weather;

/// <summary>
/// The content-addressed GFS grid registry and the attach of a profile's <see cref="WeatherProfile.WindGridHash"/> to its
/// grid. The registry is process-wide, so every class that touches it runs in the one collection.
/// </summary>
[Collection(WindGridRegistryCollection.Name)]
public sealed class WindGridRegistryTests
{
    private const string LiveMetar = "KOAK 141153Z 29012KT 10SM FEW015 18/11 A2992";

    public WindGridRegistryTests() => WindGridRegistry.ResetForTests();

    [Fact]
    public void Register_SameBytesTwice_ReturnsOneHashAndStoresOnce()
    {
        byte[] sample = Grib2ReaderTests.LoadSample();

        string first = WindGridRegistry.Register(sample);
        string second = WindGridRegistry.Register([.. sample]);

        Assert.Equal(first, second);
        Assert.Equal(1, WindGridRegistry.Count);
        Assert.True(WindGridRegistry.TryGetBytes(first, out ReadOnlyMemory<byte> stored));
        Assert.Equal(sample, stored.ToArray());
    }

    [Fact]
    public void Register_HashIsSha256OfTheBytes_LowercaseHex()
    {
        byte[] sample = Grib2ReaderTests.LoadSample();

        string hash = WindGridRegistry.Register(sample);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(sample)), hash);
        Assert.Equal(64, hash.Length);
        Assert.True(WindGridRegistry.TryGetField(hash, out GriddedWindField? field));
        Assert.Equal(20, field.LevelsHpa.Count);
    }

    [Fact]
    public void Register_BytesThatDoNotDecode_Throws()
    {
        byte[] sample = Grib2ReaderTests.LoadSample();

        Assert.Throws<Grib2FormatException>(() => WindGridRegistry.Register(sample[..(sample.Length - 100)]));
        Assert.Equal(0, WindGridRegistry.Count);
    }

    [Fact]
    public void Unpinned_EvictsLeastRecentlyUsed_PastEight()
    {
        List<byte[]> grids = DistinctGrids(9);
        List<string> hashes = [.. grids.Take(8).Select(WindGridRegistry.Register)];
        Assert.True(WindGridRegistry.TryGetField(hashes[0], out _));

        string ninth = WindGridRegistry.Register(grids[8]);

        Assert.Equal(8, WindGridRegistry.Count);
        Assert.False(WindGridRegistry.TryGetBytes(hashes[1], out _));
        Assert.True(WindGridRegistry.TryGetBytes(hashes[0], out _));
        Assert.True(WindGridRegistry.TryGetBytes(ninth, out _));
    }

    [Fact]
    public void Pinned_IsNeverEvicted()
    {
        List<byte[]> grids = DistinctGrids(10);
        string pinned = WindGridRegistry.Register(grids[0]);
        IDisposable pin = WindGridRegistry.Pin(pinned);
        List<string> others = [.. grids.Skip(1).Select(WindGridRegistry.Register)];

        // Nine of the ten are held: the pinned grid, the oldest of all, and the eight newest unpinned. Reading the pinned
        // one would mark it used, so its presence is read from the count.
        Assert.False(WindGridRegistry.TryGetBytes(others[0], out _));
        Assert.Equal(9, WindGridRegistry.Count);

        pin.Dispose();

        Assert.Equal(8, WindGridRegistry.Count);
        Assert.False(WindGridRegistry.TryGetBytes(pinned, out _));
    }

    [Fact]
    public void ReadBrotliWithinLimit_DecompressionBomb_ReturnsNullWithoutInflatingIt()
    {
        byte[] bomb = BrotliOfZeros(64 * 1024 * 1024);
        Assert.True(bomb.Length < WindGridRegistry.MaxGridBytes, $"the bomb is {bomb.Length} bytes compressed");
        using var compressed = new MemoryStream(bomb);

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[]? grib2 = WindGridRegistry.ReadBrotliWithinLimit(compressed);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(grib2);
        // The bound is 4 × MaxGridBytes (2 MiB): the read holds one MaxGridBytes + 1 buffer and the decoder's own state, while
        // inflating the whole stream before checking its length allocates at least the 64 MiB it expands to.
        Assert.True(allocated <= 4L * WindGridRegistry.MaxGridBytes, $"the read allocated {allocated:N0} bytes");
    }

    [Fact]
    public void ParseV1_LiveProfileWithRegisteredHash_AttachesTheGrid()
    {
        string hash = WindGridRegistry.Register(Grib2ReaderTests.LoadSample());
        Assert.True(WindGridRegistry.TryGetField(hash, out GriddedWindField? field));

        WeatherProfile profile = ParseProfile(LiveWeatherJson(hash));

        Assert.Equal(hash, profile.WindGridHash);
        Assert.Same(field, profile.WindGrid);
    }

    [Fact]
    public void ParseV1_HashWithNoEntry_FliesTheLayers()
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Grib2ReaderTests.LoadSample()));

        var warnings = WarningLogCapture.Install();

        WeatherProfile profile = ParseProfile(LiveWeatherJson(hash));

        Assert.Equal(hash, profile.WindGridHash);
        Assert.Null(profile.WindGrid);
        Assert.Equal(2, profile.WindLayers.Count);
        Assert.Contains($"GFS wind grid {hash} not available; flying the FD wind layers", warnings.Warnings);
    }

    [Fact]
    public void ParseV1_HashOnANonLiveProfile_IsIgnored()
    {
        string hash = WindGridRegistry.Register(Grib2ReaderTests.LoadSample());
        string noMetars = $$"""{"windLayers":[{"altitude":0,"direction":290,"speed":12}],"windGridHash":"{{hash}}"}""";
        string surfaceAloft =
            $$"""{"metars":["{{LiveMetar}}"],"windLayers":[{"altitude":3000,"direction":290,"speed":12}],"windGridHash":"{{hash}}"}""";

        var warnings = WarningLogCapture.Install();

        Assert.Null(ParseProfile(noMetars).WindGrid);
        Assert.Null(ParseProfile(surfaceAloft).WindGrid);
        Assert.Equal(2, warnings.Warnings.Count(w => w.Contains($"GFS wind grid {hash} on weather", StringComparison.Ordinal)));
    }

    [Fact]
    public void RegisterAndPin_IntoAFullRegistry_IsNeverEvicted()
    {
        List<byte[]> grids = DistinctGrids(18);
        List<string> filled = [.. grids.Take(8).Select(WindGridRegistry.Register)];

        IDisposable pin = WindGridRegistry.RegisterAndPin(grids[8], out string pinned);
        List<string> later = [.. grids.Skip(9).Select(WindGridRegistry.Register)];

        Assert.Equal(WindGridRegistry.HashOf(grids[8]), pinned);
        Assert.Equal(9, WindGridRegistry.Count);
        Assert.All(filled, hash => Assert.False(WindGridRegistry.TryGetBytes(hash, out _)));
        Assert.True(WindGridRegistry.TryGetField(pinned, out _));

        // Registering held bytes with a pin pins the entry already there.
        IDisposable second = WindGridRegistry.RegisterAndPin(grids[8], out _);
        pin.Dispose();
        WindGridRegistry.Register(grids[0]);
        Assert.True(WindGridRegistry.TryGetBytes(pinned, out _));

        second.Dispose();
        Assert.Equal(8, WindGridRegistry.Count);
        Assert.Contains(later[^1], later.Where(hash => WindGridRegistry.TryGetBytes(hash, out _)));
    }

    [Fact]
    public void PinTakenBeforeAReset_ReleasesNothingAfterIt()
    {
        List<byte[]> grids = DistinctGrids(10);
        IDisposable stale = WindGridRegistry.RegisterAndPin(grids[0], out string hash);
        WindGridRegistry.ResetForTests();
        using IDisposable current = WindGridRegistry.RegisterAndPin(grids[0], out _);

        stale.Dispose();
        foreach (byte[] grid in grids.Skip(1))
        {
            WindGridRegistry.Register(grid);
        }

        Assert.True(WindGridRegistry.TryGetBytes(hash, out _));
    }

    [Fact]
    public void ProfileWithoutGrid_SerializesExactlyAsBefore()
    {
        var profile = new WeatherProfile
        {
            Id = "a1",
            ArtccId = "ZOA",
            Name = "Live Weather (12:00Z)",
            Metars = [LiveMetar],
        };

        string json = JsonSerializer.Serialize(profile);

        Assert.Equal(
            $$"""{"id":"a1","artccId":"ZOA","name":"Live Weather (12:00Z)","precipitation":null,"windLayers":[],"metars":["{{LiveMetar}}"]}""",
            json
        );

        profile.WindGridHash = "ab";
        Assert.EndsWith(
            """
            ,"windGridHash":"ab"}
            """,
            JsonSerializer.Serialize(profile)
        );
    }

    internal static string LiveWeatherJson(string hash) =>
        $$"""
            {
              "id": "live", "artccId": "ZOA", "name": "Live Weather (12:00Z)", "metars": ["{{LiveMetar}}"],
              "windLayers": [{ "altitude": 0, "direction": 290, "speed": 12 }, { "altitude": 12000, "direction": 270, "speed": 40 }],
              "windGridHash": "{{hash}}"
            }
            """;

    /// <summary>One A320 at 11,000 ft east of Oakland, heading west inside the GFS sample's box (lat 34–42, lon −126..−117).</summary>
    internal const string AirborneNearOakScenarioJson = """
        {
          "id": "grid", "name": "Grid", "primaryAirportId": "OAK",
          "aircraft": [{
            "id": "ac1", "aircraftId": "DAL45", "aircraftType": "A320",
            "startingConditions": {
              "type": "Coordinates", "coordinates": { "lat": 37.75, "lon": -121.9 },
              "altitude": 11000, "speed": 250, "heading": 270, "navigationPath": ""
            },
            "flightplan": { "rules": "VFR", "departure": "KSAC", "destination": "KOAK", "cruiseAltitude": 11000, "route": "" }
          }]
        }
        """;

    internal static WeatherProfile ParseProfile(string json)
    {
        WeatherParseResult result = WeatherTimelineParser.Parse(json);
        Assert.Null(result.Error);
        return Assert.IsType<WeatherProfile>(result.Profile);
    }

    /// <summary>A Brotli stream that expands to <paramref name="length"/> zero bytes, written in 1 MiB chunks.</summary>
    private static byte[] BrotliOfZeros(int length)
    {
        byte[] chunk = new byte[1024 * 1024];
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int written = 0; written < length; written += chunk.Length)
            {
                brotli.Write(chunk);
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// <paramref name="count"/> different, decodable GRIB2 files cut from the real sample: file k is the sample without the
    /// messages of its k-th isobaric level.
    /// </summary>
    internal static List<byte[]> DistinctGrids(int count)
    {
        byte[] sample = Grib2ReaderTests.LoadSample();
        List<(double Level, byte[] Bytes)> messages = [];
        int offset = 0;
        while (offset < sample.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt64BigEndian(sample.AsSpan(offset + 8)));
            byte[] message = sample[offset..(offset + length)];
            messages.Add((Grib2Reader.Read(message)[0].FirstSurfaceValue, message));
            offset += length;
        }

        List<double> levels = [.. messages.Select(m => m.Level).Distinct()];
        Assert.True(levels.Count >= count, $"the sample has {levels.Count} levels, fewer than {count}");
        return [.. levels.Take(count).Select(dropped => messages.Where(m => m.Level != dropped).SelectMany(m => m.Bytes).ToArray())];
    }
}

/// <summary>The xUnit collection of every test class that registers, resets or reads the process-wide <see cref="WindGridRegistry"/>.</summary>
public static class WindGridRegistryCollection
{
    public const string Name = "WindGridRegistry";
}
