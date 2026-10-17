using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Weather;

namespace Yaat.Client.Tests;

public class SavedWeatherGridTests
{
    private static byte[] LoadSample() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData/gfs-zoa-sample.grib2"));

    /// <summary>A live-shaped profile (METARs, surface layer at 0 ft) naming the sample grid, as live weather sends it.</summary>
    private static string LiveJson(byte[] grib2) =>
        JsonSerializer.Serialize(
            new WeatherProfile
            {
                Id = "a1b2c3d4",
                ArtccId = "ZOA",
                Name = "Live Weather (16:00Z)",
                WindLayers =
                [
                    new WindLayer
                    {
                        Id = "surface",
                        Altitude = 0,
                        Direction = 256,
                        Speed = 8,
                    },
                    new WindLayer
                    {
                        Id = "fd-3000",
                        Altitude = 3000,
                        Direction = 266,
                        Speed = 12,
                    },
                ],
                Metars = ["METAR KOAK 081953Z 27008KT 10SM FEW200 21/12 A2999"],
                WindGridHash = WindGridRegistry.HashOf(grib2),
            }
        );

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Copies <paramref name="source"/> with one property renamed, keeping every other property as it was.</summary>
    private static JsonObject RenameKey(JsonObject source, string from, string to)
    {
        JsonObject renamed = [];
        foreach (KeyValuePair<string, JsonNode?> pair in source)
        {
            renamed[pair.Key == from ? to : pair.Key] = pair.Value?.DeepClone();
        }

        return renamed;
    }

    /// <summary>The property names in <paramref name="root"/> that spell <paramref name="property"/> in any casing.</summary>
    private static List<string> KeysLike(JsonObject root, string property) =>
        [.. root.Select(pair => pair.Key).Where(key => string.Equals(key, property, StringComparison.OrdinalIgnoreCase))];

    [Fact]
    public void EmbedThenExtract_RoundTripsTheBytesAndLeavesTheProfileUntouched()
    {
        byte[] sample = LoadSample();
        string live = LiveJson(sample);

        string saved = SavedWeatherGrid.Embed(live, sample);
        SavedWeatherGridExtract extract = SavedWeatherGrid.Extract(saved);

        Assert.Equal(Convert.ToBase64String(sample), Parse(saved)["windGridGrib2"]!.GetValue<string>());
        Assert.Equal(sample, extract.Grib2);
        Assert.False(extract.GridMissing);
        Assert.True(JsonNode.DeepEquals(Parse(live), Parse(extract.Json)), extract.Json);
    }

    [Fact]
    public void Extract_HashWithoutEmbeddedBytes_StripsTheHash()
    {
        string live = LiveJson(LoadSample());

        SavedWeatherGridExtract extract = SavedWeatherGrid.Extract(live);

        Assert.Null(extract.Grib2);
        Assert.True(extract.GridMissing);
        JsonObject sent = Parse(extract.Json);
        Assert.False(sent.ContainsKey("windGridHash"));
        Assert.Equal("Live Weather (16:00Z)", sent["name"]!.GetValue<string>());
    }

    [Fact]
    public void Extract_CorruptBase64_StripsBothFieldsAndReturnsNoBytes()
    {
        JsonObject saved = Parse(LiveJson(LoadSample()));
        saved["windGridGrib2"] = "not*base64!";

        SavedWeatherGridExtract extract = SavedWeatherGrid.Extract(saved.ToJsonString());

        Assert.Null(extract.Grib2);
        Assert.True(extract.GridMissing);
        JsonObject sent = Parse(extract.Json);
        Assert.False(sent.ContainsKey("windGridHash"));
        Assert.False(sent.ContainsKey("windGridGrib2"));
    }

    [Fact]
    public void Extract_NoGridFields_ReturnsTheJsonWithNothingMissing()
    {
        JsonObject plain = Parse(LiveJson(LoadSample()));
        plain.Remove("windGridHash");

        SavedWeatherGridExtract extract = SavedWeatherGrid.Extract(plain.ToJsonString());

        Assert.Null(extract.Grib2);
        Assert.False(extract.GridMissing);
        Assert.True(JsonNode.DeepEquals(plain, Parse(extract.Json)));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("""{"bytes":"AAAA"}""")]
    public void Extract_NonStringGrid_CountsAsMissingAndStripsTheHash(string embedded)
    {
        JsonObject saved = Parse(LiveJson(LoadSample()));
        saved["windGridGrib2"] = JsonNode.Parse(embedded);

        SavedWeatherGridExtract extract = SavedWeatherGrid.Extract(saved.ToJsonString());

        Assert.Null(extract.Grib2);
        Assert.True(extract.GridMissing);
        Assert.Equal("grid not in the saved file", SavedWeatherGrid.NotInSavedFile);
        JsonObject sent = Parse(extract.Json);
        Assert.False(sent.ContainsKey("windGridHash"));
        Assert.False(sent.ContainsKey("windGridGrib2"));
    }

    // -------------------------------------------------------------------------
    // NamedHash / WithHash: the hash a JSON names, and rewriting it
    // -------------------------------------------------------------------------

    [Fact]
    public void NamedHash_LiveJson_ReturnsItsHash()
    {
        byte[] sample = LoadSample();

        Assert.Equal(WindGridRegistry.HashOf(sample), SavedWeatherGrid.NamedHash(LiveJson(sample)));
    }

    [Theory]
    [InlineData("""{"name":"Custom Weather","windLayers":[]}""")]
    [InlineData("[1,2]")]
    [InlineData("{")]
    [InlineData(null)]
    public void NamedHash_NoHashOrNotAnObject_ReturnsNull(string? json) => Assert.Null(SavedWeatherGrid.NamedHash(json));

    [Fact]
    public void WithHash_NewHash_ReplacesOnlyTheHash()
    {
        JsonObject live = Parse(LiveJson(LoadSample()));

        JsonObject rewritten = Parse(SavedWeatherGrid.WithHash(live.ToJsonString(), "0123abcd"));

        Assert.Equal("0123abcd", rewritten["windGridHash"]!.GetValue<string>());
        live["windGridHash"] = "0123abcd";
        Assert.True(JsonNode.DeepEquals(live, rewritten));
    }

    [Fact]
    public void WithHash_JsonWithoutAHash_AddsIt()
    {
        JsonObject plain = Parse(LiveJson(LoadSample()));
        plain.Remove("windGridHash");

        JsonObject rewritten = Parse(SavedWeatherGrid.WithHash(plain.ToJsonString(), "0123abcd"));

        Assert.Equal("0123abcd", rewritten["windGridHash"]!.GetValue<string>());
        plain["windGridHash"] = "0123abcd";
        Assert.True(JsonNode.DeepEquals(plain, rewritten));
    }

    [Fact]
    public void WithHash_Null_StripsTheHashAndKeepsTheRest()
    {
        JsonObject live = Parse(LiveJson(LoadSample()));

        JsonObject rewritten = Parse(SavedWeatherGrid.WithHash(live.ToJsonString(), null));

        Assert.False(rewritten.ContainsKey("windGridHash"));
        live.Remove("windGridHash");
        Assert.True(JsonNode.DeepEquals(live, rewritten));
    }

    // The WeatherChanged echo keeps the held grid bytes only while the room's weather names the same grid.

    [Fact]
    public void NamesGrid_EchoNamingTheHeldHash_KeepsTheGrid()
    {
        byte[] sample = LoadSample();

        Assert.True(SavedWeatherGrid.NamesGrid(LiveJson(sample), WindGridRegistry.HashOf(sample)));
    }

    [Fact]
    public void NamesGrid_EchoNamingAnotherHash_ClearsTheGrid()
    {
        string other = SavedWeatherGrid.WithHash(LiveJson(LoadSample()), "0123abcd");

        Assert.False(SavedWeatherGrid.NamesGrid(other, WindGridRegistry.HashOf(LoadSample())));
    }

    [Theory]
    [InlineData("""{"name":"Custom Weather","windLayers":[]}""")]
    [InlineData(null)]
    public void NamesGrid_EchoNamingNoHash_ClearsTheGrid(string? sourceJson) =>
        Assert.False(SavedWeatherGrid.NamesGrid(sourceJson, WindGridRegistry.HashOf(LoadSample())));

    // The weather parser matches property names case-insensitively, so a saved profile may spell the grid keys any way.

    [Fact]
    public void Extract_DifferentlyCasedGridKeys_ReturnsTheEmbeddedGrid()
    {
        byte[] sample = LoadSample();
        JsonObject saved = RenameKey(Parse(LiveJson(sample)), "windGridHash", "WindGridHash");
        saved["WindGridGrib2"] = Convert.ToBase64String(sample);

        SavedWeatherGridExtract extract = SavedWeatherGrid.Extract(saved.ToJsonString());

        Assert.Equal(sample, extract.Grib2);
        Assert.False(extract.GridMissing);
        JsonObject sent = Parse(extract.Json);
        Assert.Empty(KeysLike(sent, "windGridGrib2"));
        Assert.Single(KeysLike(sent, "windGridHash"));
    }

    [Fact]
    public void Extract_DifferentlyCasedHashWithoutBytes_StripsIt()
    {
        JsonObject saved = RenameKey(Parse(LiveJson(LoadSample())), "windGridHash", "WindGridHash");

        SavedWeatherGridExtract extract = SavedWeatherGrid.Extract(saved.ToJsonString());

        Assert.Null(extract.Grib2);
        Assert.True(extract.GridMissing);
        Assert.Empty(KeysLike(Parse(extract.Json), "windGridHash"));
    }

    [Fact]
    public void NamedHash_DifferentlyCasedKey_ReturnsItsHash()
    {
        byte[] sample = LoadSample();
        string json = RenameKey(Parse(LiveJson(sample)), "windGridHash", "WindGridHash").ToJsonString();

        Assert.Equal(WindGridRegistry.HashOf(sample), SavedWeatherGrid.NamedHash(json));
    }

    [Fact]
    public async Task Save_DifferentlyCasedHash_FetchesTheGridAndWritesTheCanonicalKeys()
    {
        byte[] sample = LoadSample();
        string hash = WindGridRegistry.HashOf(sample);
        string json = RenameKey(Parse(LiveJson(sample)), "windGridHash", "WindGridHash").ToJsonString();
        List<string> fetched = [];

        WeatherSaveContents saved = await WeatherSaveGrid.PrepareAsync(
            json,
            null,
            h =>
            {
                fetched.Add(h);
                return Task.FromResult(sample.ToArray());
            },
            NullLogger.Instance
        );

        Assert.Equal([hash], fetched);
        Assert.Null(saved.StrippedReason);
        JsonObject contents = Parse(saved.Contents);
        Assert.Single(KeysLike(contents, "windGridHash"));
        Assert.Single(KeysLike(contents, "windGridGrib2"));
        Assert.Equal(Convert.ToBase64String(sample), contents["windGridGrib2"]!.GetValue<string>());
        Assert.Equal(sample, SavedWeatherGrid.Extract(saved.Contents).Grib2);
    }

    [Fact]
    public async Task Save_ProfileSpellingBothKeys_LeavesExactlyOneOfEach()
    {
        byte[] sample = LoadSample();
        string hash = WindGridRegistry.HashOf(sample);
        JsonObject profile = RenameKey(Parse(LiveJson(sample)), "windGridHash", "WindGridHash");
        profile["WindGridGrib2"] = Convert.ToBase64String(sample);

        WeatherSaveContents saved = await WeatherSaveGrid.PrepareAsync(
            profile.ToJsonString(),
            ActiveWindGrid.FromGrib2(sample, hash),
            _ => throw new InvalidOperationException("fetched"),
            NullLogger.Instance
        );

        Assert.Null(saved.StrippedReason);
        JsonObject contents = Parse(saved.Contents);
        string hashKey = Assert.Single(KeysLike(contents, "windGridHash"));
        Assert.Equal(hash, contents[hashKey]!.GetValue<string>());
        Assert.Equal(["windGridGrib2"], KeysLike(contents, "windGridGrib2"));
        Assert.Equal(Convert.ToBase64String(sample), contents["windGridGrib2"]!.GetValue<string>());
    }

    [Fact]
    public void WithHash_DifferentlyCasedKey_ReplacesItWithOneCanonicalKey()
    {
        JsonObject live = RenameKey(Parse(LiveJson(LoadSample())), "windGridHash", "WindGridHash");

        JsonObject rewritten = Parse(SavedWeatherGrid.WithHash(live.ToJsonString(), "0123abcd"));

        Assert.Equal(["windGridHash"], KeysLike(rewritten, "windGridHash"));
        Assert.Equal("0123abcd", rewritten["windGridHash"]!.GetValue<string>());
    }

    [Fact]
    public void WithHash_Null_DifferentlyCasedKey_RemovesIt()
    {
        JsonObject live = RenameKey(Parse(LiveJson(LoadSample())), "windGridHash", "WindGridHash");

        JsonObject rewritten = Parse(SavedWeatherGrid.WithHash(live.ToJsonString(), null));

        Assert.Empty(KeysLike(rewritten, "windGridHash"));
    }

    [Fact]
    public void WeatherEditor_LiveJsonWithAnEmbeddedGrid_OutputsNeitherGridField()
    {
        byte[] sample = LoadSample();
        string saved = SavedWeatherGrid.Embed(LiveJson(sample), sample);

        string edited = WeatherTimelineEditorViewModel.FromJson(saved).BuildJson();

        JsonObject output = Parse(edited);
        Assert.False(output.ContainsKey("windGridHash"), edited);
        Assert.False(output.ContainsKey("windGridGrib2"), edited);
    }
}
