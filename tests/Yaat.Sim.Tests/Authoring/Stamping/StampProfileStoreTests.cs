using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Stamping;

namespace Yaat.Sim.Tests.Authoring.Stamping;

/// <summary>
/// <see cref="StampProfile"/> JSON and <see cref="StampProfileStore"/> over a per-test folder: round trip, save and list,
/// replace by id, and a corrupt file reported by name.
/// </summary>
public sealed class StampProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-stamp-profile-tests", Guid.NewGuid().ToString("N"));

    public StampProfileStoreTests()
    {
        TestVnasData.EnsureInitialized();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Json_RoundTrip_KeepsEveryField()
    {
        StampProfile profile = FullProfile("p1", "SFO arrivals");

        var read = StampProfile.FromJson(profile.ToJson());

        Assert.Equal(profile.ToJson(), read.ToJson());
        Assert.Equal(profile.StarAliases, read.StarAliases);
        Assert.Equal(profile.Presets.Commands, read.Presets.Commands);
        Assert.Equal(profile.Pools.CallsignPrefixes, read.Pools.CallsignPrefixes);
        Assert.Equal(profile.StartingConditions, read.StartingConditions);
        Assert.Equal(profile.FlightPlan, read.FlightPlan);
        Assert.Equal(profile.AutoTrack, read.AutoTrack);
        Assert.Equal(profile.ExpectedApproach, read.ExpectedApproach);
    }

    [Fact]
    public void Json_HasNoRoutePoints()
    {
        JsonObject json = JsonNode.Parse(FullProfile("p1", "SFO arrivals").ToJson())!.AsObject();

        Assert.Equal(
            [
                "id",
                "name",
                "heavyOnly",
                "starAliases",
                "startingConditions",
                "flightPlan",
                "presets",
                "autoTrack",
                "primaryAirport",
                "expectedApproach",
                "pools",
            ],
            json.Select(p => p.Key)
        );
        Assert.DoesNotContain(PropertyNames(json), name => name.Contains("point", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Save_ThenList_ReturnsTheProfile()
    {
        var store = new StampProfileStore(_root);
        StampProfile profile = FullProfile("p1", "SFO arrivals");

        store.Save(profile);
        StampProfileListing listing = store.List();

        Assert.Empty(listing.Problems);
        Assert.Equal(profile.ToJson(), Assert.Single(listing.Profiles).ToJson());
        Assert.Equal(profile.ToJson(), store.Load("p1")!.ToJson());
    }

    [Fact]
    public void Save_TwiceWithOneId_Replaces()
    {
        var store = new StampProfileStore(_root);

        store.Save(FullProfile("p1", "first"));
        store.Save(FullProfile("p1", "second"));

        Assert.Equal("second", Assert.Single(store.List().Profiles).Name);
    }

    [Fact]
    public void List_CorruptFile_IsReportedByNameAndOthersLoad()
    {
        var store = new StampProfileStore(_root);
        store.Save(FullProfile("good", "good one"));
        File.WriteAllText(Path.Combine(_root, "bad.json"), "{ not json");

        StampProfileListing listing = store.List();

        Assert.Equal("good", Assert.Single(listing.Profiles).Id);
        StoreProblem problem = Assert.Single(listing.Problems);
        Assert.Equal("bad.json", problem.FileName);
    }

    [Theory]
    [InlineData("null-aliases", """{"id":"null-aliases","starAliases":null}""")]
    [InlineData("alias-no-from", """{"id":"alias-no-from","starAliases":[{"to":"STLER3"}]}""")]
    [InlineData("preset-no-command", """{"id":"preset-no-command","presets":{"commands":[{"timeOffset":0}]}}""")]
    public void List_MissingRequiredValue_IsAProblem(string id, string json)
    {
        var store = new StampProfileStore(_root);
        store.Save(FullProfile("good", "good one"));
        File.WriteAllText(Path.Combine(_root, id + ".json"), json);

        StampProfileListing listing = store.List();

        Assert.Equal("good", Assert.Single(listing.Profiles).Id);
        Assert.Equal(id + ".json", Assert.Single(listing.Problems).FileName);
    }

    [Fact]
    public void Load_IdDifferingOnlyByCase_ReturnsTheProfile()
    {
        var store = new StampProfileStore(_root);
        store.Save(FullProfile("P1", "upper"));

        Assert.Equal("upper", store.Load("p1")!.Name);
    }

    [Fact]
    public void Save_IdDifferingOnlyByCase_ReplacesTheProfile()
    {
        var store = new StampProfileStore(_root);
        store.Save(FullProfile("P1", "upper"));

        store.Save(FullProfile("p1", "lower"));

        StampProfile only = Assert.Single(store.List().Profiles);
        Assert.Equal(("p1", "lower"), (only.Id, only.Name));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("CON")]
    [InlineData("")]
    public void InvalidId_Throws(string id)
    {
        var store = new StampProfileStore(_root);

        Assert.Throws<ArgumentException>(() => store.Save(FullProfile(id, "bad")));
        Assert.Throws<ArgumentException>(() => store.Load(id));
        Assert.Throws<ArgumentException>(() => store.Delete(id));
    }

    [Fact]
    public void Load_MissingId_ReturnsNull() => Assert.Null(new StampProfileStore(_root).Load("nothing"));

    [Fact]
    public void Load_CorruptFile_ThrowsInvalidDataWithTheCause()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "bad.json"), "{ not json");

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => new StampProfileStore(_root).Load("bad"));

        Assert.Contains("bad.json", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Delete_ExistingThenMissing_ReturnsTrueThenFalse()
    {
        var store = new StampProfileStore(_root);
        store.Save(FullProfile("p1", "one"));

        Assert.True(store.Delete("p1"));
        Assert.False(store.Delete("p1"));
        Assert.Empty(store.List().Profiles);
    }

    [Fact]
    public void List_IdNotMatchingFileName_IsAProblem()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "other.json"), FullProfile("p9", "moved").ToJson());

        StampProfileListing listing = new StampProfileStore(_root).List();

        Assert.Empty(listing.Profiles);
        Assert.Equal("other.json", Assert.Single(listing.Problems).FileName);
    }

    [Fact]
    public void List_SortsByNameThenId()
    {
        var store = new StampProfileStore(_root);
        store.Save(FullProfile("c", "beta"));
        store.Save(FullProfile("b", "alpha"));
        store.Save(FullProfile("a", "beta"));

        Assert.Equal(["b", "a", "c"], store.List().Profiles.Select(p => p.Id));
    }

    [Fact]
    public void DefaultRoot_IsStampProfilesUnderTheDataFolder() =>
        Assert.Equal(Path.GetFullPath(YaatPaths.Combine("stamp-profiles")), new StampProfileStore().Root);

    private static IEnumerable<string> PropertyNames(JsonNode? node) =>
        node switch
        {
            JsonObject obj => obj.SelectMany(p => PropertyNames(p.Value).Prepend(p.Key)),
            JsonArray array => array.SelectMany(PropertyNames),
            _ => [],
        };

    private static StampProfile FullProfile(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            HeavyOnly = true,
            StarAliases = [new StarAlias("BDEGA4", "STLER3")],
            StartingConditions = new StampStartingConditions
            {
                Apply = true,
                Type = "Coordinates",
                Altitude = 11000,
                Speed = 280,
                Heading = 135,
                ApplyNavigationPath = true,
                Runway = "28L",
                Parking = "A1",
            },
            FlightPlan = new StampFlightPlan
            {
                Apply = true,
                Rules = "IFR",
                Departure = "KLAX",
                Destination = "KSFO",
                ApplyRoute = true,
                CruiseAltitude = 35000,
                CruiseSpeed = 450,
                Remarks = "/V/",
            },
            Presets = new StampPresets
            {
                Apply = true,
                Replace = true,
                Commands = [new StampPresetCommand("DM {ALT}", 30), new StampPresetCommand("SPD 250", 0)],
            },
            AutoTrack = new StampAutoTrack
            {
                Apply = true,
                PositionId = "01HPOSITION",
                HandoffDelay = 60,
                ScratchPad = "BDG",
                InterimAltitude = "110",
                ClearedAltitude = "240",
            },
            PrimaryAirport = "KSFO",
            ExpectedApproach = "ILS28L",
            Pools = new StampPools
            {
                AircraftTypes = ["B738", "A320"],
                CallsignPrefixes = ["UAL", "SWA"],
                Runways = ["28L", "28R"],
                ParkingSpots = ["A1", "A2"],
            },
        };
}
