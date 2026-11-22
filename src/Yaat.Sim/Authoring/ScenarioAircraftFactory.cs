using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring;

/// <summary>
/// Builds the aircraft the Scenario Editor adds to a scenario: a new one with the editor's defaults, and a duplicate of an
/// existing one. Neither inserts the aircraft; the caller does, inside its edit.
/// </summary>
public static class ScenarioAircraftFactory
{
    private static readonly ILogger Log = SimLog.CreateLogger("ScenarioAircraftFactory");

    /// <summary>
    /// A new aircraft: a fresh id, no callsign, transponder C, no spawn delay, no flight plan, no airport of its own (the
    /// loader places a parking spawn at the scenario's primary airport), and a <c>Parking</c> start with no stand named yet.
    /// </summary>
    /// <remarks>
    /// The aircraft is read from JSON holding only those members, so a scenario document writes only them, never the
    /// other members' defaults.
    /// </remarks>
    /// <param name="scenario">The scenario the aircraft is for; read only.</param>
    /// <param name="now">The clock reading the id is made from.</param>
    /// <param name="random">The source of the id's random bits.</param>
    /// <returns>The new aircraft.</returns>
    public static ScenarioAircraft NewAircraft(Scenario scenario, DateTimeOffset now, Random random)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return ReadAircraft(
            new JsonObject
            {
                ["id"] = ScenarioIdGenerator.Create(now, random),
                ["aircraftId"] = "",
                ["transponderMode"] = "C",
                ["spawnDelay"] = 0,
                ["startingConditions"] = new JsonObject { ["type"] = StartingConditionKinds.Parking, ["parking"] = "" },
            }
        );
    }

    /// <summary>
    /// Reads one aircraft from its JSON with the scenario document's own reader, so each object's presence record is what
    /// the JSON held.
    /// </summary>
    internal static ScenarioAircraft ReadAircraft(JsonObject aircraft) =>
        ScenarioDocument.Read(new JsonObject { ["aircraft"] = new JsonArray(aircraft) }.ToJsonString()).Scenario.Aircraft[0];

    /// <summary>
    /// A deep copy of <paramref name="source"/>, made through the scenario document's own JSON so unmodelled members
    /// survive, with a new id, a new id for each preset command (in order), and a callsign no aircraft in
    /// <paramref name="scenario"/> has: a trailing number is counted up (keeping its width), else 2, 3, ... is appended,
    /// and a blank callsign stays blank.
    /// </summary>
    /// <param name="scenario">The scenario whose callsigns the copy's must differ from; read only.</param>
    /// <param name="source">The aircraft to copy; left unchanged.</param>
    /// <param name="now">The clock reading the new ids are made from.</param>
    /// <param name="random">The source of the new ids' random bits: the aircraft's first, then each preset's.</param>
    /// <returns>The copy.</returns>
    public static ScenarioAircraft Duplicate(Scenario scenario, ScenarioAircraft source, DateTimeOffset now, Random random)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(source);
        string json = ScenarioDocument.FromScenario(new Scenario { Aircraft = [source] }).Write();
        ScenarioAircraft copy = ScenarioDocument.Read(json).Scenario.Aircraft[0];

        copy.Id = ScenarioIdGenerator.Create(now, random);
        foreach (PresetCommand preset in copy.PresetCommands)
        {
            preset.Id = ScenarioIdGenerator.Create(now, random);
        }

        copy.AircraftId = UniqueCallsign(scenario, source.AircraftId);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Duplicated aircraft {Source} as {Copy}", source.AircraftId, copy.AircraftId);
        }

        return copy;
    }

    private static string UniqueCallsign(Scenario scenario, string callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            return callsign;
        }

        var taken = new HashSet<string>(scenario.Aircraft.Select(a => a.AircraftId), StringComparer.OrdinalIgnoreCase);
        (string stem, long number, string format) = SuffixOf(callsign);
        string candidate;
        do
        {
            number++;
            candidate = stem + number.ToString(format, CultureInfo.InvariantCulture);
        } while (taken.Contains(candidate));

        return candidate;
    }

    // The text before a callsign's trailing number, that number and its width as a format; a callsign without one (or
    // with one too long to count) is the stem whole, counting from 1 so the first suffix tried is 2.
    private static (string Stem, long Number, string Format) SuffixOf(string callsign)
    {
        int digitsAt = callsign.Length;
        while ((digitsAt > 0) && char.IsAsciiDigit(callsign[digitsAt - 1]))
        {
            digitsAt--;
        }

        string digits = callsign[digitsAt..];
        if ((digits.Length > 0) && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long number))
        {
            return (callsign[..digitsAt], number, "D" + digits.Length.ToString(CultureInfo.InvariantCulture));
        }

        return (callsign, 1, "D1");
    }
}
