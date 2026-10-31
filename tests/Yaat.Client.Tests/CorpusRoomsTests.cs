using Xunit;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>A corpus folder's <c>rooms.json</c>: each feed's airport, facility and seed, and the telephony and type maps.</summary>
public sealed class CorpusRoomsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("corpus-rooms-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Read_NoFile_ReturnsNull() => Assert.Null(CorpusRooms.Read(_dir));

    [Fact]
    public void Read_ParsesFeedsTelephonyAndTypes()
    {
        Write(
            """
            { "telephony": { "SKYSHARE": "skyshare", "QXE": "horizon" },
              "types": { "N4DL": "C172" },
              "feeds": { "koak-app": { "airport": "KOAK", "facility": "app", "seed": 343001 },
                         "koak-gnd": { "airport": "KOAK", "facility": "gnd", "seed": 7 } } }
            """
        );

        var rooms = CorpusRooms.Read(_dir);

        Assert.NotNull(rooms);
        Assert.Equal("horizon", rooms.Telephony["qxe"]);
        Assert.Equal("skyshare", rooms.Telephony["SKYSHARE"]);
        Assert.Equal("C172", rooms.Types["n4dl"]);
        Assert.Equal(new CorpusRoomFeed("KOAK", "app", 343001), rooms.Feeds["koak-app"]);
        Assert.Equal(new CorpusRoomFeed("KOAK", "gnd", 7), rooms.Feeds["koak-gnd"]);
        Assert.False(rooms.Feeds.ContainsKey("KOAK-APP"));
    }

    [Fact]
    public void Read_TypesAndTelephonyOptional()
    {
        Write("""{ "feeds": { "koak-twr": { "airport": "KOAK", "facility": "twr", "seed": 1 } } }""");

        var rooms = CorpusRooms.Read(_dir);

        Assert.NotNull(rooms);
        Assert.Empty(rooms.Telephony);
        Assert.Empty(rooms.Types);
        Assert.Equal(new CorpusRoomFeed("KOAK", "twr", 1), rooms.Feeds["koak-twr"]);
    }

    [Theory]
    [InlineData("tower")]
    [InlineData("APP")]
    public void Read_BadFacility_ThrowsNamingFeed(string facility)
    {
        Write($$"""{ "feeds": { "koak-app": { "airport": "KOAK", "facility": "{{facility}}", "seed": 1 } } }""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => CorpusRooms.Read(_dir));

        Assert.Contains("koak-app", error.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_dir, CorpusRooms.FileName), error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "feeds": {} }""")]
    [InlineData("""{ "telephony": { "QXE": "horizon" } }""")]
    [InlineData("""null""")]
    [InlineData("""[1, 2]""")]
    public void Read_NoFeeds_Throws(string json)
    {
        Write(json);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => CorpusRooms.Read(_dir));

        Assert.Contains(Path.Combine(_dir, CorpusRooms.FileName), error.Message, StringComparison.Ordinal);
    }

    private void Write(string json) => File.WriteAllText(Path.Combine(_dir, CorpusRooms.FileName), json);
}
