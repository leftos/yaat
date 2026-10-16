using System.Text.Json;
using System.Text.Json.Nodes;
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
