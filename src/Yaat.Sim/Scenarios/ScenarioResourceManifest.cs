using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Scenarios;

/// <summary>
/// The network resources a scenario load needs, read from the scenario JSON before anything is fetched: the scenario's
/// ARTCC config, the configs of the other ARTCCs its ATC roster names, and every airport whose ground layout the load or
/// the scenario's traffic can read. The JSON is deserialized with <see cref="ScenarioLoader"/>'s own options, and the
/// per-aircraft airport choice comes from <see cref="ScenarioLoader.LayoutAirportIds"/>, so the manifest cannot miss an
/// airport the loader reads. Airport ids are keyed by <see cref="AirportLayoutDownloader.ToFaaCode"/>, so <c>KOAK</c>
/// and <c>oak</c> are one entry; ARTCC ids are upper-cased. <c>ScenarioResourceManifestTests</c>' corpus test is the drift
/// guard: it loads every committed scenario and fails when the loader asks for a layout the manifest did not name.
/// </summary>
public sealed class ScenarioResourceManifest
{
    private static readonly ILogger Log = SimLog.CreateLogger("ScenarioResourceManifest");

    private ScenarioResourceManifest(string? readError, string scenarioName, string? artccId, List<string> neighbourArtccIds, List<string> airportIds)
    {
        ReadError = readError;
        ScenarioName = scenarioName;
        ArtccId = artccId;
        NeighbourArtccIds = neighbourArtccIds;
        AirportIds = airportIds;
    }

    /// <summary>The parser's message when the scenario JSON could not be read; null when it was.</summary>
    public string? ReadError { get; }

    /// <summary>The scenario's <c>name</c>; empty when the JSON could not be read.</summary>
    public string ScenarioName { get; }

    /// <summary>The scenario's own ARTCC (<c>artccId</c>), upper-cased; null when the scenario names none.</summary>
    public string? ArtccId { get; }

    /// <summary>Every distinct <c>atc[].artccId</c> other than <see cref="ArtccId"/>, upper-cased, in roster order.</summary>
    public IReadOnlyList<string> NeighbourArtccIds { get; }

    /// <summary>
    /// Every airport whose ground layout the scenario needs, FAA-coded and distinct, in the order found: the primary
    /// airport; per aircraft its <c>airportId</c>, departure, destination, the airports
    /// <see cref="ScenarioLoader.LayoutAirportIds"/> names and the airport each preset command carries (a new destination,
    /// an approach's airport); then each VFR arrival generator's <c>directTo</c> that is an airport.
    /// </summary>
    public IReadOnlyList<string> AirportIds { get; }

    /// <summary>
    /// Reads <paramref name="json"/> into a manifest. Never throws on bad input: a JSON that cannot be deserialized
    /// yields a manifest with <see cref="ReadError"/> set and no resources. Reads <see cref="NavigationDatabase.Instance"/>
    /// to tell an airport from a fix in VFR generator targets and preset commands.
    /// </summary>
    public static ScenarioResourceManifest FromJson(string json)
    {
        Scenario? scenario;
        try
        {
            scenario = JsonSerializer.Deserialize<Scenario>(json, ScenarioLoader.JsonOptions);
        }
        catch (JsonException ex)
        {
            Log.LogWarning(ex, "Scenario JSON could not be read for its resource manifest");
            return Unreadable(ex.Message);
        }

        if (scenario is null)
        {
            return Unreadable("Failed to deserialize scenario JSON");
        }

        string? artccId = string.IsNullOrWhiteSpace(scenario.ArtccId) ? null : scenario.ArtccId.Trim().ToUpperInvariant();
        return new ScenarioResourceManifest(
            readError: null,
            scenario.Name ?? "",
            artccId,
            CollectNeighbourArtccIds(scenario, artccId),
            CollectAirportIds(scenario, NavigationDatabase.Instance)
        );
    }

    private static ScenarioResourceManifest Unreadable(string message) => new(message, scenarioName: "", artccId: null, [], []);

    private static List<string> CollectNeighbourArtccIds(Scenario scenario, string? ownArtccId)
    {
        var ids = new List<string>();
        foreach (ScenarioAtc? atc in scenario.Atc ?? [])
        {
            if (string.IsNullOrWhiteSpace(atc?.ArtccId))
            {
                continue;
            }

            string id = atc.ArtccId.Trim().ToUpperInvariant();
            if ((id != ownArtccId) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static List<string> CollectAirportIds(Scenario scenario, NavigationDatabase navDb)
    {
        var ids = new List<string>();
        AddAirport(ids, scenario.PrimaryAirportId);

        foreach (ScenarioAircraft? ac in scenario.Aircraft ?? [])
        {
            if (ac is not null)
            {
                AddAircraftAirports(ids, ac, scenario.PrimaryAirportId, navDb);
            }
        }

        foreach (VfrArrivalGeneratorConfig? generator in scenario.VfrArrivalGenerators ?? [])
        {
            AddIfAirport(ids, generator?.DirectTo, navDb);
        }

        return ids;
    }

    private static void AddAircraftAirports(List<string> ids, ScenarioAircraft ac, string? primaryAirportId, NavigationDatabase navDb)
    {
        AddAirport(ids, ac.AirportId);
        AddAirport(ids, ac.FlightPlan?.Departure);
        AddAirport(ids, ac.FlightPlan?.Destination);
        if (ac.StartingConditions is not null)
        {
            foreach (string? airportId in ScenarioLoader.LayoutAirportIds(ac, primaryAirportId))
            {
                AddAirport(ids, airportId);
            }
        }

        foreach (PresetCommand? preset in ac.PresetCommands ?? [])
        {
            AddPresetAirports(ids, preset?.Command, navDb);
        }
    }

    /// <summary>
    /// Parses the preset with <see cref="CommandParser.ParseCompound"/>, the parser the preset runs through at spawn, and
    /// names the airport of each command that carries one. A preset that does not parse names nothing.
    /// </summary>
    private static void AddPresetAirports(List<string> ids, string? command, NavigationDatabase navDb)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(command);
        if (parsed.Value is not { } compound)
        {
            return;
        }

        foreach (ParsedBlock block in compound.Blocks)
        {
            foreach (ParsedCommand parsedCommand in block.Commands)
            {
                AddIfAirport(ids, CommandAirport(parsedCommand), navDb);
            }
        }
    }

    /// <summary>
    /// The airport a command sends the aircraft to or clears it into: a new destination, a military-route exit
    /// clearance's destination (an airport or a fix), or an approach's airport.
    /// </summary>
    private static string? CommandAirport(ParsedCommand command) =>
        command switch
        {
            ChangeDestinationCommand c => c.Airport,
            ClearedOutOfMilitaryRouteCommand c => c.Destination,
            _ => ApproachAirport(command),
        };

    private static string? ApproachAirport(ParsedCommand command) =>
        command switch
        {
            ClearedApproachCommand c => c.AirportCode,
            JoinApproachCommand c => c.AirportCode,
            ClearedApproachStraightInCommand c => c.AirportCode,
            JoinApproachStraightInCommand c => c.AirportCode,
            ExpectApproachCommand c => c.AirportCode,
            ClearedVisualApproachCommand c => c.AirportCode,
            _ => null,
        };

    private static void AddIfAirport(List<string> ids, string? token, NavigationDatabase navDb)
    {
        if (navDb.TryResolveAirport(token, out string canonicalId))
        {
            AddAirport(ids, canonicalId);
        }
    }

    private static void AddAirport(List<string> ids, string? airportId)
    {
        if (string.IsNullOrWhiteSpace(airportId))
        {
            return;
        }

        string faaCode = AirportLayoutDownloader.ToFaaCode(airportId.Trim());
        if (!ids.Contains(faaCode))
        {
            ids.Add(faaCode);
        }
    }
}
