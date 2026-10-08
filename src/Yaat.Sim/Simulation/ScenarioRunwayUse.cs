using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation;

/// <summary>
/// What a loaded scenario already decides about runways at one airport: how many of its aircraft already have a runway
/// (those starting on the ground count as <paramref name="Departures"/>, the airborne ones as <paramref name="Arrivals"/>)
/// and the runways its arrival generators feed (<paramref name="GeneratorArrivalRunways"/>, designators in ordinal order).
/// The load prompt tells the mentor that these keep their runway and only the rest are offered the active runways.
/// </summary>
public sealed record ScenarioRunwayUse(int Departures, int Arrivals, IReadOnlyList<string> GeneratorArrivalRunways)
{
    /// <summary>
    /// Counts the aircraft of <paramref name="result"/> (immediate and delayed) whose loaded phases carry an assigned
    /// runway, at that runway's airport, and lists the primary airport's arrival-generator runways, read as
    /// <see cref="ImpliedActiveRunways.For"/> reads them (a generator whose runway does not resolve is skipped). Keyed by
    /// FAA id; an airport with neither is absent. Deferred aircraft count nothing. Read <paramref name="result"/> as
    /// <see cref="ScenarioLoader.Load"/> returned it, before the engine dispatches the preset commands.
    /// </summary>
    public static IReadOnlyDictionary<string, ScenarioRunwayUse> CountAssigned(ScenarioLoadResult result)
    {
        var departures = new Dictionary<string, int>(StringComparer.Ordinal);
        var arrivals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (LoadedAircraft loaded in result.ImmediateAircraft.Concat(result.DelayedAircraft))
        {
            if (loaded.State.Phases?.AssignedRunway is not { } runway)
            {
                continue;
            }

            Dictionary<string, int> bucket = loaded.State.IsOnGround ? departures : arrivals;
            string airport = NavigationDatabase.NormalizeAirport(runway.AirportId);
            bucket[airport] = bucket.GetValueOrDefault(airport) + 1;
        }

        (string Airport, List<string> Runways)? generated = GeneratorArrivalRunwaysAtPrimary(result);
        IEnumerable<string> airports = departures.Keys.Concat(arrivals.Keys);
        if (generated is { } primary)
        {
            airports = airports.Append(primary.Airport);
        }

        var counts = new Dictionary<string, ScenarioRunwayUse>(StringComparer.Ordinal);
        foreach (string airport in airports.Distinct(StringComparer.Ordinal))
        {
            List<string> runways = (generated is { } g) && (g.Airport == airport) ? g.Runways : [];
            counts[airport] = new ScenarioRunwayUse(departures.GetValueOrDefault(airport), arrivals.GetValueOrDefault(airport), runways);
        }

        return counts;
    }

    /// <summary>The primary airport and the distinct runways its arrival generators feed, or <c>null</c> when there are none.</summary>
    private static (string Airport, List<string> Runways)? GeneratorArrivalRunwaysAtPrimary(ScenarioLoadResult result)
    {
        if (result.PrimaryAirportId is not { Length: > 0 } primary)
        {
            return null;
        }

        var runways = new SortedSet<string>(StringComparer.Ordinal);
        foreach (ScenarioGeneratorConfig generator in result.Generators)
        {
            if ((!string.IsNullOrWhiteSpace(generator.Runway)) && (NavigationDatabase.Instance.GetRunway(primary, generator.Runway) is { } runway))
            {
                runways.Add(runway.Designator);
            }
        }

        return runways.Count > 0 ? (NavigationDatabase.NormalizeAirport(primary), [.. runways]) : null;
    }
}
