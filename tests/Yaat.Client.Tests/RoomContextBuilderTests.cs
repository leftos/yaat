using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Speech.Context;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>
/// The corpus rooms a feed's clips are decoded against: the unpadded room of the feed's real aircraft, the room padded
/// with seeded distractors, the clip's oracle, and the hotwords and form coverage read from them.
/// </summary>
public sealed class RoomContextBuilderTests
{
    private static readonly Lazy<AirportGroundLayout> OakLayout = new(() => AirportLayoutFile.Load(Path.Combine("TestData", "oak.geojson"), "KOAK"));

    private static readonly CorpusRooms Rooms = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["QXE"] = "horizon" },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["N4926F"] = "C172" },
        new Dictionary<string, CorpusRoomFeed>(StringComparer.Ordinal) { ["koak-gnd"] = new("KOAK", "gnd", 1), ["koak-app"] = new("KOAK", "app", 2) }
    );

    private static readonly Dictionary<string, IReadOnlyList<CorpusSlot>> Slots = new(StringComparer.Ordinal)
    {
        ["koak-gnd_seg0001"] =
        [
            Slot(EntityKind.Callsign, "SWA2495", "southwest twenty four ninety five"),
            Slot(EntityKind.Taxiway, "KOAK/W4", "whiskey four"),
            Slot(EntityKind.Runway, "KOAK/30", "runway three zero"),
        ],
        ["koak-gnd_seg0002"] =
        [
            Slot(EntityKind.Callsign, "SWA2495", "southwest twenty four ninety five"),
            Slot(EntityKind.Landmark, "KOAK/KAISER", "kaiser"),
        ],
        ["koak-gnd_seg0003"] =
        [
            Slot(EntityKind.Callsign, "N4926F", "cessna four nine two six foxtrot"),
            Slot(EntityKind.Taxiway, "KOAK/W4", "whiskey four"),
        ],
        ["koak-gnd_seg0004"] = [Slot(EntityKind.Callsign, "SWA", "southwest"), Slot(EntityKind.Runway, "KOAK/30", "runway three zero")],
        ["koak-app_seg0001"] =
        [
            Slot(EntityKind.Callsign, "QXE2101", "horizon twenty one zero one"),
            Slot(EntityKind.Procedure, "KHWD/R28L", "r nav two eight left"),
            Slot(EntityKind.Runway, "KHWD/28L", "runway two eight left"),
        ],
        ["koak-app_seg0002"] = [Slot(EntityKind.Callsign, "QXE2101", "horizon twenty one zero one"), Slot(EntityKind.Fix, "MITOE", "mitoe")],
    };

    private static readonly string[] NounWords = ["runway", "taxiway", "gate", "parking", "spot", "approach", "departure", "arrival"];

    public RoomContextBuilderTests() => Yaat.Sim.Testing.TestVnasData.EnsureInitialized();

    [Fact]
    public void Navdata_HasKoakApproaches() =>
        Assert.Contains(Builder().AirportEntities("koak-gnd"), entity => (entity.Kind == EntityKind.Procedure) && (entity.Value == "KOAK/I30"));

    [Fact]
    public void LeaveOneOut_OwnEntitiesComeFromOtherClipsOnly()
    {
        RoomContextBuilder builder = Builder();

        RoomContexts first = builder.For("koak-gnd_seg0001");
        RoomContexts second = builder.For("koak-gnd_seg0002");

        Assert.Equal(["KOAK/KAISER"], OwnValues(first.Unpadded, "SWA2495"));
        Assert.Equal(["KOAK/KAISER"], OwnValues(first.Padded, "SWA2495"));
        Assert.Equal(["KOAK/W4", "KOAK/30"], OwnValues(second.Unpadded, "SWA2495"));
        Assert.Equal(["KOAK/W4", "KOAK/30"], OwnValues(second.Padded, "SWA2495"));
    }

    [Fact]
    public void Unpadded_HoldsOnlyBuildableGoldCallsigns()
    {
        RoomContextBuilder builder = Builder();

        TransmissionContext unpadded = builder.For("koak-gnd_seg0001").Unpadded;

        Assert.Equal(["N4926F", "SWA2495"], unpadded.Aircraft.Select(aircraft => aircraft.Callsign.Value).Order(StringComparer.Ordinal));
        Assert.Equal(["SWA"], builder.UnbuiltCallsigns("koak-gnd"));
        Assert.Equal("KOAK", unpadded.AirportId);
    }

    [Fact]
    public void Padded_Is35AircraftAndDeterministic()
    {
        TransmissionContext padded = Builder().For("koak-gnd_seg0001").Padded;
        TransmissionContext again = Builder().For("koak-gnd_seg0001").Padded;

        string[] callsigns = [.. padded.Aircraft.Select(aircraft => aircraft.Callsign.Value)];
        Assert.Equal(RoomContextBuilder.PaddedRoomSize, callsigns.Length);
        Assert.Contains("SWA2495", callsigns);
        Assert.Contains("N4926F", callsigns);
        Assert.Equal(callsigns, again.Aircraft.Select(aircraft => aircraft.Callsign.Value));
    }

    [Theory]
    [InlineData("koak-gnd_seg0001", new[] { "SWA2495", "N4926F" }, new[] { "2495", "4926" })]
    [InlineData("koak-app_seg0001", new[] { "QXE2101" }, new[] { "2101" })]
    public void Padded_DistractorsNeverEqualOrShareRealDigits(string clipId, string[] real, string[] realDigits)
    {
        TransmissionContext padded = Builder().For(clipId).Padded;

        string[] callsigns = [.. padded.Aircraft.Select(aircraft => aircraft.Callsign.Value)];
        Assert.Equal(callsigns.Length, callsigns.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (string distractor in callsigns.Where(callsign => !real.Contains(callsign, StringComparer.OrdinalIgnoreCase)))
        {
            Assert.DoesNotContain(new string([.. distractor.Where(char.IsAsciiDigit)]), realDigits);
        }

        Assert.Equal(RoomContextBuilder.PaddedRoomSize - real.Length, callsigns.Count(callsign => !real.Contains(callsign)));
    }

    [Fact]
    public void Padded_DistractorsHaveAtMostThreeOwnEntities()
    {
        RoomContextBuilder builder = Builder();
        HashSet<string> airport = [.. builder.AirportEntities("koak-gnd").Select(entity => entity.Value)];

        TransmissionContext padded = builder.For("koak-gnd_seg0001").Padded;

        IEnumerable<RoomAircraft> distractors = padded.Aircraft.Where(aircraft => aircraft.Callsign.Value is not ("SWA2495" or "N4926F"));
        Assert.All(
            distractors,
            aircraft =>
            {
                string[] own = [.. aircraft.OwnEntities.Select(entity => entity.Value)];
                Assert.InRange(own.Length, 0, 3);
                Assert.Equal(own.Length, own.Distinct().Count());
                Assert.All(own, value => Assert.Contains(value, airport));
            }
        );
    }

    [Fact]
    public void AppRoom_IncludesHaywardRunwaysAndApproaches()
    {
        RoomContextBuilder builder = Builder();

        string[] app = [.. builder.AirportEntities("koak-app").Select(entity => entity.Value)];
        string[] gnd = [.. builder.AirportEntities("koak-gnd").Select(entity => entity.Value)];

        Assert.Contains("KHWD/R28L", app);
        Assert.Contains("KHWD/28L", app);
        Assert.DoesNotContain("KHWD/R28L", gnd);
        Assert.DoesNotContain("KHWD/28L", gnd);
        Assert.Contains("MITOE", app);
        Assert.Contains("MITOE", gnd);
    }

    [Fact]
    public void Oracle_HoldsOnlyTheClipsGold()
    {
        TransmissionContext oracle = Builder().For("koak-app_seg0001").Oracle;

        Assert.Equal(["QXE2101"], oracle.Aircraft.Select(aircraft => aircraft.Callsign.Value));
        Assert.Empty(oracle.Aircraft[0].OwnEntities);
        Assert.Equal(["KHWD/R28L", "KHWD/28L"], oracle.AirportEntities.Select(entity => entity.Value));
        Assert.Equal("KOAK", oracle.AirportId);
    }

    [Theory]
    [InlineData("ksfo-gnd_seg0001")]
    [InlineData("koak-gnd_seg0099")]
    [InlineData("not-a-clip")]
    public void UnknownFeedOrClip_Throws(string clipId)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Builder().For(clipId));

        Assert.Contains(clipId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RoomHotwords_DropNounOnlyForms()
    {
        RoomContextBuilder builder = Builder();

        IReadOnlyList<string> second = RoomHotwords.Select(builder.For("koak-gnd_seg0002").Unpadded, 10000);
        IReadOnlyList<string> first = RoomHotwords.Select(builder.For("koak-gnd_seg0001").Unpadded, 10000);

        Assert.Contains(second, phrase => phrase.StartsWith("southwest", StringComparison.Ordinal));
        Assert.DoesNotContain("runway three zero", second, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("three zero", second, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("taxiway whiskey four", second, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("whiskey four", second, StringComparer.OrdinalIgnoreCase);
        Assert.All(
            second,
            phrase =>
                Assert.Contains(
                    SpokenTokenizer.Tokenize(phrase),
                    token => (token.Kind == TokenKind.Word) && !NounWords.Contains(token.Text, StringComparer.Ordinal)
                )
        );
        Assert.Contains("kaiser", first);
    }

    [Fact]
    public void RoomHotwords_CapsAndDedupes()
    {
        TransmissionContext context = Builder().For("koak-gnd_seg0001").Padded;

        IReadOnlyList<string> phrases = RoomHotwords.Select(context, 3);
        IReadOnlyList<string> all = RoomHotwords.Select(context, 10000);

        Assert.Equal(3, phrases.Count);
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomHotwords.Select(context, 0));
    }

    [Fact]
    public void Coverage_ReachableAndFormMatched()
    {
        TransmissionContext context = Builder().For("koak-gnd_seg0001").Padded;
        CorpusSlot[] gold = [Slot(EntityKind.Runway, "KOAK/30", "runway three zero"), Slot(EntityKind.Taxiway, "KOAK/ZZ9", "zulu zulu nine")];

        IReadOnlyList<CorpusSlotCoverage> rows = RoomCoverage.Count(gold, context);

        Assert.Equal(Enum.GetValues<EntityKind>(), rows.Select(row => row.Kind));
        Assert.Equal(new CorpusSlotCoverage(EntityKind.Runway, 1, 1, 1), rows.Single(row => row.Kind == EntityKind.Runway));
        Assert.Equal(new CorpusSlotCoverage(EntityKind.Taxiway, 1, 0, 0), rows.Single(row => row.Kind == EntityKind.Taxiway));
        Assert.Equal(new CorpusSlotCoverage(EntityKind.Fix, 0, 0, 0), rows.Single(row => row.Kind == EntityKind.Fix));
    }

    private static RoomContextBuilder Builder() =>
        new(new RoomSources(Rooms, Slots, Layout, Landmarks, NavigationDatabase.Instance), DecoderSettings.Default);

    private static AirportGroundLayout? Layout(string airport) => airport == "KOAK" ? OakLayout.Value : null;

    private static IReadOnlyList<ContextEntity> Landmarks(string airport) =>
        airport == "KOAK" ? [ContextEntityFactory.Landmark("KOAK", "KAISER", ["kaiser"])] : [];

    private static string[] OwnValues(TransmissionContext context, string callsign) =>
        [.. context.Aircraft.Single(aircraft => aircraft.Callsign.Value == callsign).OwnEntities.Select(entity => entity.Value)];

    private static CorpusSlot Slot(EntityKind kind, string value, string spoken) => new(kind, value, spoken, null);
}
