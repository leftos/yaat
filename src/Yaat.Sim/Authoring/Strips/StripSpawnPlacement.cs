using Microsoft.Extensions.Logging;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Authoring.Strips;

/// <summary>
/// Edits an aircraft's spawn bay placement: the entry of <see cref="Scenario.FlightStripConfigurations"/> whose
/// <see cref="FlightStripConfiguration.AircraftIds"/> names it, which the loader turns into the bay its departure strip
/// prints into at spawn. The configurations are the placement's one home; nothing is cached on the aircraft.
/// </summary>
public static class StripSpawnPlacement
{
    private static readonly ILogger Log = SimLog.CreateLogger("StripSpawnPlacement");

    /// <summary>
    /// Places the aircraft's strip in the bay and 0-based rack: adds it to the entry with the same facility, bay and
    /// rack (a new entry, minted by <paramref name="idFactory"/>, when there is none), removes it from every other entry
    /// and drops an entry that loses its last aircraft. Refuses, writing nothing, for an aircraft the scenario does not
    /// hold, an empty facility or bay id, a negative rack or an aircraft the engine would give an arrival strip, which
    /// never prints into a bay at spawn.
    /// </summary>
    /// <returns>Null when placed; otherwise why not.</returns>
    public static string? Set(Scenario scenario, string aircraftId, string facilityId, string bayId, int rack0, Func<string> idFactory)
    {
        ArgumentNullException.ThrowIfNull(idFactory);
        string? refusal = Refusal(scenario, aircraftId, facilityId, bayId, rack0);
        if (refusal is not null)
        {
            if (Log.IsEnabled(LogLevel.Debug))
            {
                Log.LogDebug("Spawn placement for {AircraftId} refused: {Reason}", aircraftId, refusal);
            }
            return refusal;
        }

        FlightStripConfiguration? target = scenario.FlightStripConfigurations.FirstOrDefault(c =>
            string.Equals(c.FacilityId, facilityId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.BayId, bayId, StringComparison.Ordinal)
            && (c.Rack == rack0)
        );
        RemoveFrom(scenario, aircraftId, keep: target);
        if (target is null)
        {
            scenario.FlightStripConfigurations.Add(
                new FlightStripConfiguration
                {
                    Id = idFactory(),
                    FacilityId = facilityId,
                    BayId = bayId,
                    Rack = rack0,
                    AircraftIds = [aircraftId],
                }
            );
        }
        else if (!target.AircraftIds.Contains(aircraftId, StringComparer.Ordinal))
        {
            target.AircraftIds.Add(aircraftId);
        }

        return null;
    }

    /// <summary>
    /// The bay the aircraft's strip prints into at spawn, as the loader resolves it: the last entry with a bay that
    /// names the aircraft wins; null when none does or the scenario holds no such aircraft with a callsign.
    /// </summary>
    public static ScenarioStripBayAssignment? Get(Scenario scenario, string aircraftId)
    {
        bool known = scenario.Aircraft.Any(a => (a.Id == aircraftId) && (!string.IsNullOrEmpty(a.AircraftId)));
        FlightStripConfiguration? entry = scenario.FlightStripConfigurations.LastOrDefault(c =>
            (!string.IsNullOrEmpty(c.BayId)) && c.AircraftIds.Contains(aircraftId, StringComparer.Ordinal)
        );
        return (known && (entry is not null)) ? new ScenarioStripBayAssignment(entry.FacilityId ?? "", entry.BayId!, entry.Rack) : null;
    }

    /// <summary>Removes the aircraft from every entry, dropping an entry it was the last aircraft of.</summary>
    /// <returns>True when any entry named the aircraft.</returns>
    public static bool Clear(Scenario scenario, string aircraftId) => RemoveFrom(scenario, aircraftId, keep: null);

    /// <summary>Removes a deleted aircraft's id from every entry, as <see cref="Clear"/> does.</summary>
    /// <returns>True when any entry named the aircraft.</returns>
    public static bool Prune(Scenario scenario, string removedAircraftId) => Clear(scenario, removedAircraftId);

    private static string? Refusal(Scenario scenario, string aircraftId, string facilityId, string bayId, int rack0)
    {
        ScenarioAircraft? aircraft = scenario.Aircraft.FirstOrDefault(a => a.Id == aircraftId);
        if (aircraft is null)
        {
            return $"the scenario has no aircraft {aircraftId}";
        }

        // The loader skips an entry with no bay, so such a write would be lost to Get straight away.
        if (string.IsNullOrWhiteSpace(facilityId))
        {
            return "the facility id is empty";
        }

        if (string.IsNullOrWhiteSpace(bayId))
        {
            return "the bay id is empty";
        }

        if (rack0 < 0)
        {
            return $"rack {rack0 + 1} is below rack 1";
        }

        return StripCommandText.IsArrival(aircraft, scenario.PrimaryAirportId)
            ? $"{aircraft.AircraftId} is an arrival: its strip never prints into a bay at spawn"
            : null;
    }

    private static bool RemoveFrom(Scenario scenario, string aircraftId, FlightStripConfiguration? keep)
    {
        bool removed = false;
        for (int i = scenario.FlightStripConfigurations.Count - 1; i >= 0; i--)
        {
            FlightStripConfiguration entry = scenario.FlightStripConfigurations[i];
            if (ReferenceEquals(entry, keep) || (entry.AircraftIds.RemoveAll(id => id == aircraftId) == 0))
            {
                continue;
            }

            removed = true;
            if (entry.AircraftIds.Count == 0)
            {
                scenario.FlightStripConfigurations.RemoveAt(i);
            }
        }

        return removed;
    }
}
