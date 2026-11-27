using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring.Stamping;

/// <summary>
/// Expands the stamp tokens {CS}, {TYP}, {RTE}, {RMK}, {ALT}, {DEP} and {ARR} in preset command text from an aircraft's
/// callsign, type and flight plan.
/// </summary>
public static partial class StampTokens
{
    private static readonly ILogger Log = SimLog.CreateLogger("StampTokens");

    /// <summary>Replaces each known token in <paramref name="text"/>, ignoring case; an unknown token stays as written.</summary>
    /// <remarks>{ALT} is the filed cruise altitude, empty when it is 0; flight plan tokens are empty without a flight plan.</remarks>
    /// <param name="text">The text holding tokens.</param>
    /// <param name="aircraft">The aircraft whose fields the tokens read.</param>
    /// <returns>The expanded text.</returns>
    public static string Expand(string text, ScenarioAircraft aircraft) =>
        TokenPattern().Replace(text, match => ValueOf(match.Groups[1].Value, aircraft) ?? match.Value);

    private static string? ValueOf(string name, ScenarioAircraft aircraft)
    {
        ScenarioFlightPlan plan = aircraft.FlightPlan ?? new();
        string? value = name.ToUpperInvariant() switch
        {
            "CS" => aircraft.AircraftId,
            "TYP" => aircraft.AircraftType,
            "RTE" => plan.Route,
            "RMK" => plan.Remarks,
            "ALT" => plan.CruiseAltitude > 0 ? plan.CruiseAltitude.ToString(CultureInfo.InvariantCulture) : "",
            "DEP" => plan.Departure,
            "ARR" => plan.Destination,
            _ => null,
        };
        if ((value is null) && Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Unknown stamp token {{{Name}}} left as written", name);
        }

        return value;
    }

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex TokenPattern();
}
