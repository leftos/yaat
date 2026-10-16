using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Weather;

namespace Yaat.Client.Tests;

/// <summary>
/// Save Weather's file contents: the weather JSON with the grid it names embedded, fetched from the room by hash when this client
/// holds no bytes for it, or the JSON with the hash stripped and the reason when the fetch fails.
/// </summary>
public class WeatherSaveGridTests
{
    private const string HubRefusal =
        "An unexpected error occurred invoking 'GetWindGrid' on the server. HubException: This room does not hold that wind grid.";

    private static byte[] LoadSample() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData/gfs-zoa-sample.grib2"));

    /// <summary>The sample's first GRIB2 message alone: a valid grid whose hash differs from the sample's.</summary>
    private static byte[] FirstMessageOf(byte[] grib2)
    {
        int length = checked((int)BinaryPrimitives.ReadUInt64BigEndian(grib2.AsSpan(8)));
        return grib2[..length];
    }

    /// <summary>A live-shaped profile naming <paramref name="hash"/>, or no grid when null.</summary>
    private static string LiveJson(string? hash) =>
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
                ],
                Metars = ["METAR KOAK 081953Z 27008KT 10SM FEW200 21/12 A2999"],
                WindGridHash = hash,
            }
        );

    private static Func<string, Task<byte[]>> NeverFetch() => hash => throw new InvalidOperationException($"fetched {hash}");

    private static Task<WeatherSaveContents> PrepareAsync(string json, ActiveWindGrid? held, Func<string, Task<byte[]>> fetch) =>
        WeatherSaveGrid.PrepareAsync(json, held, fetch, NullLogger.Instance);

    [Fact]
    public async Task NoHash_ReturnsTheJsonAndNeverFetches()
    {
        string json = LiveJson(null);

        WeatherSaveContents saved = await PrepareAsync(json, null, NeverFetch());

        Assert.Equal(json, saved.Contents);
        Assert.Null(saved.FetchedGrid);
        Assert.Null(saved.StrippedReason);
    }

    [Fact]
    public async Task HeldGridForTheHash_EmbedsItAndNeverFetches()
    {
        byte[] sample = LoadSample();
        string hash = WindGridRegistry.HashOf(sample);
        string json = LiveJson(hash);

        WeatherSaveContents saved = await PrepareAsync(json, ActiveWindGrid.FromGrib2(sample, hash), NeverFetch());

        Assert.Equal(SavedWeatherGrid.Embed(json, sample), saved.Contents);
        Assert.Null(saved.FetchedGrid);
        Assert.Null(saved.StrippedReason);
    }

    [Fact]
    public async Task NoBytesHeld_FetchesByHashAndEmbeds()
    {
        byte[] sample = LoadSample();
        string hash = WindGridRegistry.HashOf(sample);
        List<string> fetched = [];

        WeatherSaveContents saved = await PrepareAsync(
            LiveJson(hash),
            null,
            h =>
            {
                fetched.Add(h);
                return Task.FromResult(sample.ToArray());
            }
        );

        Assert.Equal([hash], fetched);
        Assert.Null(saved.StrippedReason);
        Assert.Equal(hash, saved.FetchedGrid?.Hash);
        Assert.Equal(sample, saved.FetchedGrid?.Grib2);
        SavedWeatherGridExtract reloaded = SavedWeatherGrid.Extract(saved.Contents);
        Assert.Equal(sample, reloaded.Grib2);
        Assert.False(reloaded.GridMissing);
        Assert.Equal(hash, SavedWeatherGrid.NamedHash(reloaded.Json));
    }

    [Fact]
    public async Task HeldGridForAnotherHash_Fetches()
    {
        byte[] sample = LoadSample();
        string hash = WindGridRegistry.HashOf(sample);
        byte[] other = FirstMessageOf(sample);
        var held = ActiveWindGrid.FromGrib2(other, WindGridRegistry.HashOf(other));
        List<string> fetched = [];

        WeatherSaveContents saved = await PrepareAsync(
            LiveJson(hash),
            held,
            h =>
            {
                fetched.Add(h);
                return Task.FromResult(sample.ToArray());
            }
        );

        Assert.Equal([hash], fetched);
        Assert.Equal(sample, SavedWeatherGrid.Extract(saved.Contents).Grib2);
        Assert.Equal(hash, saved.FetchedGrid?.Hash);
    }

    [Fact]
    public async Task FetchRefused_StripsTheHashWithTheServersReason()
    {
        string json = LiveJson(WindGridRegistry.HashOf(LoadSample()));

        WeatherSaveContents saved = await PrepareAsync(json, null, _ => throw new HubException(HubRefusal));

        Assert.Equal(GfsGridFetcher.RefusalReason(HubRefusal), saved.StrippedReason);
        Assert.Equal("This room does not hold that wind grid.", saved.StrippedReason);
        AssertStripped(json, saved);
    }

    [Fact]
    public async Task NotConnected_StripsTheHash()
    {
        string json = LiveJson(WindGridRegistry.HashOf(LoadSample()));

        WeatherSaveContents saved = await PrepareAsync(json, null, _ => throw new InvalidOperationException("Not connected."));

        Assert.Equal(WeatherSaveGrid.NotConnected, saved.StrippedReason);
        Assert.Equal("not connected to the server", saved.StrippedReason);
        AssertStripped(json, saved);
    }

    [Fact]
    public async Task FetchCanceled_StripsTheHash()
    {
        string json = LiveJson(WindGridRegistry.HashOf(LoadSample()));

        WeatherSaveContents saved = await PrepareAsync(
            json,
            null,
            _ => throw new TaskCanceledException("Invocation canceled due to the underlying connection being closed.")
        );

        Assert.Equal(WeatherSaveGrid.NotConnected, saved.StrippedReason);
        AssertStripped(json, saved);
    }

    [Fact]
    public async Task FetchTimedOut_StripsTheHash()
    {
        string json = LiveJson(WindGridRegistry.HashOf(LoadSample()));

        WeatherSaveContents saved = await PrepareAsync(
            json,
            null,
            _ => throw new TimeoutException("Server timeout elapsed without receiving a message.")
        );

        Assert.Equal(WeatherSaveGrid.NotConnected, saved.StrippedReason);
        AssertStripped(json, saved);
    }

    [Fact]
    public async Task FetchTransportError_StripsTheHash()
    {
        string json = LiveJson(WindGridRegistry.HashOf(LoadSample()));

        WeatherSaveContents saved = await PrepareAsync(json, null, _ => throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely));

        Assert.Equal(WeatherSaveGrid.NotConnected, saved.StrippedReason);
        AssertStripped(json, saved);
    }

    [Fact]
    public async Task FetchConnectionClosed_StripsTheHash()
    {
        string json = LiveJson(WindGridRegistry.HashOf(LoadSample()));

        WeatherSaveContents saved = await PrepareAsync(json, null, _ => throw new IOException("The server closed the connection."));

        Assert.Equal(WeatherSaveGrid.NotConnected, saved.StrippedReason);
        AssertStripped(json, saved);
    }

    [Fact]
    public async Task FetchedBytesOverTheCap_StripsTheHash()
    {
        byte[] oversized = new byte[WindGridRegistry.MaxGridBytes + 1];
        string json = LiveJson(WindGridRegistry.HashOf(oversized));

        WeatherSaveContents saved = await PrepareAsync(json, null, _ => Task.FromResult(oversized));

        Assert.Equal(GfsGridFetcher.OverUploadLimit, saved.StrippedReason);
        AssertStripped(json, saved);
    }

    [Fact]
    public async Task FetchedBytesWithAnotherHash_StripsTheHash()
    {
        byte[] sample = LoadSample();
        string json = LiveJson(WindGridRegistry.HashOf(sample));

        WeatherSaveContents saved = await PrepareAsync(json, null, _ => Task.FromResult(FirstMessageOf(sample)));

        Assert.Equal(WeatherSaveGrid.HashMismatch, saved.StrippedReason);
        Assert.Equal("the server's grid did not match its hash", saved.StrippedReason);
        AssertStripped(json, saved);
    }

    [Fact]
    public async Task FetchedBytesUndecodable_StripsTheHash()
    {
        byte[] junk = "this is not a GRIB2 file"u8.ToArray();
        string json = LiveJson(WindGridRegistry.HashOf(junk));

        WeatherSaveContents saved = await PrepareAsync(json, null, _ => Task.FromResult(junk));

        Assert.Equal(WeatherSaveGrid.Undecodable, saved.StrippedReason);
        Assert.Equal("the server's grid could not be decoded", saved.StrippedReason);
        AssertStripped(json, saved);
    }

    private static void AssertStripped(string json, WeatherSaveContents saved)
    {
        Assert.Null(SavedWeatherGrid.NamedHash(saved.Contents));
        Assert.Null(saved.FetchedGrid);
        Assert.Equal(SavedWeatherGrid.WithHash(json, null), saved.Contents);
    }
}
