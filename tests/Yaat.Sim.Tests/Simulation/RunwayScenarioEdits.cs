using System.Text.Json.Nodes;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Loads the committed scenario examples (<c>docs/atctrainer-scenario-examples/</c>) with edits, and builds the aircraft the
/// runway-reading tests add to them: a parked-style ground spawn on OAK's ramp, an airborne spawn near OAK, and a runway
/// spawn, each with its own preset commands.
/// </summary>
internal static class RunwayScenarioEdits
{
    public const string S1Oak1 = "01H06NVK7VN8BS7MCDXHKJZ7MQ.json";
    public const string S1Oak234 = "01H08FSWF5NCDXTQMQ3BWB0BND.json";

    private static readonly string ExamplesDir = Path.Combine(TickRecorder.FindRepoRoot(), "docs", "atctrainer-scenario-examples");

    public static ScenarioLoadResult Load(string fileName) => LoadEdited(fileName, _ => { });

    /// <summary>The immediate or delayed aircraft <paramref name="callsign"/> of <paramref name="result"/>.</summary>
    public static LoadedAircraft Loaded(ScenarioLoadResult result, string callsign) =>
        result.ImmediateAircraft.Concat(result.DelayedAircraft).Single(loaded => loaded.State.Callsign == callsign);

    /// <summary>The tokens (<c>D28L</c>, <c>A30</c>, <c>28R</c> for both) of the runways one loaded aircraft is sent to, in order.</summary>
    public static string[] SignalTokens(LoadedAircraft loaded) =>
        [.. ScenarioRunwaySignals.AircraftRunways(loaded).Select(signal => new ActiveRunway(signal.Runway.Designator, signal.Use).ToToken())];

    /// <summary>Adds a runway spawn modelled on an existing aircraft (its presets included), so the loader sees a shape it knows.</summary>
    public static void AddOnRunwaySpawn(JsonObject root, string airportId, string runway)
    {
        JsonArray aircraft = root["aircraft"]!.AsArray();
        JsonNode spawn = JsonNode.Parse(aircraft[0]!.ToJsonString())!;
        spawn["aircraftId"] = "N461RW";
        spawn["airportId"] = airportId;
        spawn["spawnDelay"] = 0;
        spawn["startingConditions"] = new JsonObject { ["type"] = "OnRunway", ["runway"] = runway };
        aircraft.Add(spawn);
    }

    public static ScenarioLoadResult LoadEdited(string fileName, Action<JsonObject> mutate)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, fileName)))!;
        mutate(root.AsObject());

        return ScenarioLoader.Load(root.ToJsonString(), new NullGroundData(), new Random(7), MagneticDeclination.EvaluationDateUtc);
    }

    /// <summary>A ground spawn on OAK's ramp (the spot S2-OAK-4's N436MS starts on): on the ground, no runway.</summary>
    public static JsonObject OakRamp() =>
        new()
        {
            ["type"] = "Coordinates",
            ["coordinates"] = new JsonObject { ["lat"] = 37.725646, ["lon"] = -122.204694 },
            ["altitude"] = 9,
            ["speed"] = 0,
            ["heading"] = 133,
        };

    /// <summary>An airborne spawn northwest of OAK, off any route: no runway.</summary>
    public static JsonObject Airborne() =>
        new()
        {
            ["type"] = "Coordinates",
            ["coordinates"] = new JsonObject { ["lat"] = 37.8, ["lon"] = -122.3 },
            ["altitude"] = 5000,
            ["speed"] = 150,
            ["heading"] = 270,
        };

    public static JsonObject OnRunway(string runway) => new() { ["type"] = "OnRunway", ["runway"] = runway };

    /// <summary>An immediate C172 at <paramref name="airportId"/>, filed to <paramref name="destination"/>, with no presets yet.</summary>
    public static JsonObject Aircraft(string callsign, string airportId, JsonObject start, string destination) =>
        new()
        {
            ["id"] = callsign,
            ["aircraftId"] = callsign,
            ["aircraftType"] = "C172",
            ["transponderMode"] = "C",
            ["airportId"] = airportId,
            ["startingConditions"] = start,
            ["flightplan"] = new JsonObject
            {
                ["rules"] = "VFR",
                ["departure"] = "KOAK",
                ["destination"] = destination,
                ["cruiseAltitude"] = 3500,
                ["cruiseSpeed"] = 110,
                ["route"] = "",
                ["remarks"] = "",
                ["aircraftType"] = "C172",
            },
            ["presetCommands"] = new JsonArray(),
            ["spawnDelay"] = 0,
            ["difficulty"] = "Easy",
        };

    /// <summary>Gives <paramref name="aircraft"/> the preset commands <paramref name="presets"/>, in order, and adds it to the scenario.</summary>
    public static void Add(JsonObject root, JsonObject aircraft, params string[] presets)
    {
        var commands = new JsonArray();
        for (int i = 0; i < presets.Length; i++)
        {
            commands.Add(
                new JsonObject
                {
                    ["id"] = $"{aircraft["aircraftId"]}-{i}",
                    ["command"] = presets[i],
                    ["timeOffset"] = 0,
                }
            );
        }

        aircraft["presetCommands"] = commands;
        root["aircraft"]!.AsArray().Add(aircraft);
    }
}
