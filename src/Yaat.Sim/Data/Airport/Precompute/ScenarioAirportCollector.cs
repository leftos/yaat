using System.Text.Json;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// The airports a set of training scenarios uses, the set the committed precompute cache covers: each scenario's primary
/// airport (<see cref="Scenario.PrimaryAirportId"/>) and every scenario aircraft's airport
/// (<see cref="ScenarioAircraft.AirportId"/>). A flight plan's departure and destination are not counted. Ids are folded
/// to the FAA form the layout downloader keys maps on (<see cref="AirportLayoutDownloader.ToFaaCode"/>), deduplicated and
/// sorted ordinal.
/// </summary>
public static class ScenarioAirportCollector
{
    /// <summary>Reads a scenario JSON with the options every scenario reader shares.</summary>
    /// <param name="json">The scenario JSON.</param>
    /// <returns>The scenario.</returns>
    /// <exception cref="InvalidDataException">The JSON is <c>null</c>.</exception>
    public static Scenario Parse(string json) =>
        JsonSerializer.Deserialize<Scenario>(json, ScenarioLoader.JsonOptions) ?? throw new InvalidDataException("The scenario JSON is null");

    /// <summary>The airports <paramref name="scenarios"/> use, by the rule in the class summary.</summary>
    /// <param name="scenarios">The scenarios.</param>
    /// <returns>The FAA ids, distinct, sorted ordinal.</returns>
    public static IReadOnlyList<string> AirportsOf(IEnumerable<Scenario> scenarios) =>
        Normalize(scenarios.SelectMany(s => s.Aircraft.Select(a => a.AirportId).Prepend(s.PrimaryAirportId)));

    /// <summary>
    /// <paramref name="airportIds"/> folded to FAA ids (trimmed, upper-cased, a four-letter K id's K dropped), blanks left
    /// out, distinct, sorted ordinal.
    /// </summary>
    /// <param name="airportIds">The ids, FAA or ICAO, null or blank allowed.</param>
    /// <returns>The FAA ids.</returns>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?> airportIds) =>
        [
            .. airportIds
                .OfType<string>()
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => AirportLayoutDownloader.ToFaaCode(id.Trim()))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
}
