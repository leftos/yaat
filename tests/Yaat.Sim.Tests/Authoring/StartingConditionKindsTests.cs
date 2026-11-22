using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Authoring;

/// <summary>
/// <see cref="StartingConditionKinds"/> holds the five starting-condition kinds <see cref="ScenarioLoader"/> reads, each with
/// the members its load path reads, and switches an aircraft's conditions from one kind to another.
/// </summary>
public class StartingConditionKindsTests
{
    private const string ExtraKey = "vnasExtra";

    private static readonly string[] AllMembers =
    [
        "type",
        "fix",
        "coordinates",
        "runway",
        "altitude",
        "speed",
        "navigationPath",
        "heading",
        "parking",
        "distanceFromRunway",
    ];

    public static TheoryData<string> OtherThanParking() => new("OnRunway", "Coordinates", "FixOrFrd", "OnFinal");

    public static TheoryData<string> EveryKind() => new("Parking", "OnRunway", "Coordinates", "FixOrFrd", "OnFinal");

    [Fact]
    public void Kinds_AreTheFiveTheLoaderReads_InCaseTableOrder() =>
        Assert.Equal(["Parking", "OnRunway", "Coordinates", "FixOrFrd", "OnFinal"], StartingConditionKinds.Kinds);

    [Fact]
    public void FieldsOf_Parking_IsTheParkingName() => Assert.Equal(["type", "parking"], StartingConditionKinds.FieldsOf("Parking"));

    [Fact]
    public void FieldsOf_OnRunway_IsTheRunway() => Assert.Equal(["type", "runway"], StartingConditionKinds.FieldsOf("OnRunway"));

    [Fact]
    public void FieldsOf_Coordinates_IsThePositionAndItsFlightState() =>
        Assert.Equal(["type", "coordinates", "altitude", "speed", "navigationPath", "heading"], StartingConditionKinds.FieldsOf("Coordinates"));

    [Fact]
    public void FieldsOf_FixOrFrd_IsTheFixAndItsFlightState() =>
        Assert.Equal(["type", "fix", "altitude", "speed", "navigationPath", "heading"], StartingConditionKinds.FieldsOf("FixOrFrd"));

    [Fact]
    public void FieldsOf_OnFinal_IsTheRunwayDistanceAltitudeAndSpeed() =>
        Assert.Equal(["type", "runway", "altitude", "speed", "distanceFromRunway"], StartingConditionKinds.FieldsOf("OnFinal"));

    [Theory]
    [MemberData(nameof(OtherThanParking))]
    public void Switch_FromAKindToParking_ClearsWhatParkingDoesNotUse(string kind) => AssertSwitched(Full(kind), "Parking");

    [Theory]
    [MemberData(nameof(OtherThanParking))]
    public void Switch_FromParkingToAKind_ClearsWhatTheKindDoesNotUse(string kind) => AssertSwitched(Full("Parking"), kind);

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Switch_ToTheSameKind_ReturnsAnEqualCopy(string kind)
    {
        StartingConditions current = Full(kind);

        StartingConditions switched = StartingConditionKinds.Switch(current, kind);

        Assert.NotSame(current, switched);
        Assert.True(JsonNode.DeepEquals(ToNode(current), ToNode(switched)));
        Assert.True(JsonNode.DeepEquals(Written(current), Written(switched)), "the switched conditions write differently");
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Switch_WrittenAfterSave_HoldsNoNullMember(string kind)
    {
        StartingConditions sparse = Full("Coordinates");
        sparse.Speed = null;
        sparse.Heading = null;

        Assert.Empty(ScenarioAircraftFactoryTests.NullMembers(Written(StartingConditionKinds.Switch(sparse, kind)), "startingConditions"));
        Assert.Empty(ScenarioAircraftFactoryTests.NullMembers(Written(StartingConditionKinds.Switch(Full("Parking"), kind)), "startingConditions"));
    }

    [Fact]
    public void Switch_CopiesCoordinatesAndExtensionData_RatherThanSharingThem()
    {
        StartingConditions current = Full("FixOrFrd");

        StartingConditions switched = StartingConditionKinds.Switch(current, "Coordinates");

        Assert.NotNull(switched.Coordinates);
        Assert.NotSame(current.Coordinates, switched.Coordinates);
        Assert.NotNull(switched.ExtensionData);
        Assert.NotSame(current.ExtensionData, switched.ExtensionData);
    }

    // The conditions as a scenario document writes them, on an aircraft built in code.
    private static JsonNode Written(StartingConditions conditions) =>
        ScenarioAircraftFactoryTests.WrittenAircraft(new ScenarioAircraft { StartingConditions = conditions })["startingConditions"]
        ?? throw new InvalidOperationException("No startingConditions written");

    [Fact]
    public void Switch_ToAnUnknownKind_ThrowsNamingTheKinds()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => StartingConditionKinds.Switch(Full("Parking"), "Airborne"));

        Assert.All(StartingConditionKinds.Kinds, k => Assert.Contains(k, ex.Message, StringComparison.Ordinal));
    }

    private static void AssertSwitched(StartingConditions current, string target)
    {
        StartingConditions switched = StartingConditionKinds.Switch(current, target);

        JsonObject before = ToNode(current);
        JsonObject after = ToNode(switched);
        IReadOnlyList<string> uses = StartingConditionKinds.FieldsOf(target);
        Assert.Equal(target, switched.Type);
        foreach (string member in AllMembers.Where(m => m != "type"))
        {
            JsonNode? expected = uses.Contains(member) ? before[member] : null;
            Assert.True(JsonNode.DeepEquals(expected, after[member]), $"{member} after {current.Type} -> {target}");
        }

        Assert.NotNull(switched.ExtensionData);
        Assert.Equal("42", switched.ExtensionData[ExtraKey].GetRawText());
    }

    private static StartingConditions Full(string kind) =>
        new()
        {
            Type = kind,
            Fix = "SUNOL",
            Coordinates = new ScenarioCoordinates { Lat = 37.7, Lon = -122.2 },
            Runway = "28R",
            Altitude = 3000,
            Speed = 210,
            NavigationPath = "SUNOL OAK",
            Heading = 280,
            Parking = "A1",
            DistanceFromRunway = 8,
            ExtensionData = new Dictionary<string, JsonElement> { [ExtraKey] = JsonDocument.Parse("42").RootElement },
        };

    private static JsonObject ToNode(StartingConditions conditions) =>
        JsonSerializer.SerializeToNode(conditions)?.AsObject() ?? throw new InvalidOperationException("Conditions serialised to null");
}
