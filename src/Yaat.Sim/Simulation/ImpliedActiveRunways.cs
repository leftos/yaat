using Yaat.Sim.ControllerAi;
using Yaat.Sim.ControllerAi.Knowledge;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation;

/// <summary>
/// The active runways a loaded scenario implies, before an instructor names any: the runway ends its spawns and
/// arrival generators can only be working, else the primary airport's facility knowledge or the generic
/// runway-in-use rule. A pure read of the loader's output — no engine, no room — so the server can call it right
/// after the room's weather is applied.
/// </summary>
public static class ImpliedActiveRunways
{
    /// <summary>
    /// The partner lookup a load passes: a scenario load has no session configuration or partner decision to couple
    /// to.
    /// </summary>
    private static readonly Func<string, string?> NoPartnerConfiguration = static _ => null;

    /// <summary>
    /// The runway ends <paramref name="result"/> implies, keyed by airport and ordinal-sorted by designator within
    /// each: its runway spawns, the runways its aircraft's expected approaches and preset commands send them to
    /// (<see cref="ScenarioRunwaySignals.AircraftRunways"/>), and its arrival generators' runways. An airport any signal
    /// names gets exactly those ends; only the primary airport, and only when nothing implies an end there, falls back
    /// to facility knowledge and then to the generic rule for the weather in force (<paramref name="weather"/> null is
    /// calm). A scenario load carries no session configuration (<see cref="ControllerAiConfig.RunwayConfigurations"/>)
    /// and no partner airport's decision, so neither is consulted. Deferred aircraft imply nothing. Read
    /// <paramref name="result"/> as <see cref="ScenarioLoader.Load"/> returned it, before the engine dispatches the
    /// preset commands: a handler rewrites <see cref="AircraftState.Phases"/>, and this only parses them.
    /// </summary>
    public static ActiveRunways For(ScenarioLoadResult result, WeatherProfile? weather, DateTime magneticModelDateUtc)
    {
        Dictionary<string, List<ActiveRunway>> implied = CollectAircraftSignals(result);
        CollectGeneratorSignals(result, implied);

        var byAirport = new Dictionary<string, List<ActiveRunway>>(StringComparer.Ordinal);
        foreach ((string airport, List<ActiveRunway> ends) in implied)
        {
            byAirport[airport] = [.. ends.OrderBy(runway => runway.Designator, StringComparer.Ordinal)];
        }

        if (result.PrimaryAirportId is { Length: > 0 } primary)
        {
            string key = NavigationDatabase.NormalizeAirport(primary);
            if ((!byAirport.ContainsKey(key)) && (ResolvePrimary(primary, weather, magneticModelDateUtc) is { Count: > 0 } resolved))
            {
                byAirport[key] = resolved;
            }
        }

        ActiveRunways runways = ActiveRunways.Empty;
        foreach ((string airport, List<ActiveRunway> ends) in byAirport)
        {
            runways = runways.With(airport, ends);
        }

        return runways;
    }

    /// <summary>
    /// What a room actually starts on, out of <paramref name="implied"/>: the airports whose implied ends cover every
    /// physical runway there — a runway counts as covered when either of its two ends is named, whatever the use —
    /// kept verbatim, and nothing else. An airport with no runway data is dropped. With no scenario sidecar the
    /// implied guess only pre-fills the mentor's prompt; the room takes it as its active runways only where it names
    /// every runway, where it is a complete statement of the field's flow.
    /// </summary>
    public static ActiveRunways RoomDefault(ActiveRunways implied)
    {
        ActiveRunways runways = ActiveRunways.Empty;
        foreach (string airport in implied.Airports)
        {
            IReadOnlyList<ActiveRunway> ends = implied.For(airport);
            IReadOnlyList<RunwayInfo> pavements = RunwayOccupancy.AirportRunways(airport);
            if ((pavements.Count > 0) && CoversEveryRunway(pavements, ends))
            {
                runways = runways.With(airport, ends);
            }
        }

        return runways;
    }

    private static bool CoversEveryRunway(IReadOnlyList<RunwayInfo> pavements, IReadOnlyList<ActiveRunway> ends) =>
        pavements.All(pavement => ends.Any(end => pavement.Id.Contains(end.Designator)));

    /// <summary>
    /// An <c>OnRunway</c> spawn's initial phase departs its end and an <c>OnFinal</c> spawn's arrives on one; then every
    /// runway the aircraft's expected approach and presets send it to, with its use.
    /// </summary>
    private static Dictionary<string, List<ActiveRunway>> CollectAircraftSignals(ScenarioLoadResult result)
    {
        var implied = new Dictionary<string, List<ActiveRunway>>(StringComparer.Ordinal);
        foreach (LoadedAircraft loaded in result.ImmediateAircraft.Concat(result.DelayedAircraft))
        {
            if (
                (loaded.State.Phases is { } phases)
                && (phases.AssignedRunway is { } runway)
                && (phases.Phases.Count > 0)
                && (InitialUse(phases.Phases[0]) is { } use)
            )
            {
                Add(implied, runway.AirportId, runway.Designator, use);
            }

            foreach (AircraftRunwaySignal signal in ScenarioRunwaySignals.AircraftRunwaysInLoad(result, loaded))
            {
                Add(implied, signal.Runway.AirportId, signal.Runway.Designator, signal.Use);
            }
        }

        return implied;
    }

    private static ActiveRunwayUse? InitialUse(Phase initial) =>
        initial switch
        {
            LinedUpAndWaitingPhase => ActiveRunwayUse.Departure,
            FinalApproachPhase => ActiveRunwayUse.Arrival,
            _ => null,
        };

    /// <summary>An arrival generator's runway is the end it feeds onto final; nothing else on a generator implies one.</summary>
    private static void CollectGeneratorSignals(ScenarioLoadResult result, Dictionary<string, List<ActiveRunway>> implied)
    {
        if (result.PrimaryAirportId is not { Length: > 0 } primary)
        {
            return;
        }

        foreach (string designator in ScenarioRunwaySignals.GeneratorArrivalRunways(result))
        {
            Add(implied, primary, designator, ActiveRunwayUse.Arrival);
        }
    }

    /// <summary>The airport's facility knowledge, else the generic rule: one end, worked both ways.</summary>
    private static List<ActiveRunway> ResolvePrimary(string primaryAirportId, WeatherProfile? weather, DateTime magneticModelDateUtc)
    {
        IReadOnlyList<RunwayInfo> runways = RunwayOccupancy.AirportRunways(primaryAirportId);
        (RunwayUseDecision? known, _) = FacilityRunwayKnowledge.Select(
            FacilityOpsDatabase.For(primaryAirportId),
            new FacilityRunwayQuery(primaryAirportId, weather, NoPartnerConfiguration, runways, magneticModelDateUtc)
        );
        if (known is not null)
        {
            return MergeUse(known.DepartureRunways, known.ArrivalRunways);
        }

        RunwayUseDecision? generic = RunwayInUseResolver.Resolve(primaryAirportId, overrideRunway: null, weather, runways, magneticModelDateUtc);
        return generic is null ? [] : [new ActiveRunway(generic.PrimaryDepartureRunway, ActiveRunwayUse.Both)];
    }

    private static List<ActiveRunway> MergeUse(IReadOnlyList<string> departureRunways, IReadOnlyList<string> arrivalRunways)
    {
        var use = new Dictionary<string, ActiveRunwayUse>(StringComparer.Ordinal);
        foreach (string end in departureRunways)
        {
            use[end] = ActiveRunwayUse.Departure;
        }

        foreach (string end in arrivalRunways)
        {
            use[end] = use.ContainsKey(end) ? ActiveRunwayUse.Both : ActiveRunwayUse.Arrival;
        }

        return [.. use.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new ActiveRunway(entry.Key, entry.Value))];
    }

    /// <summary>Files an implied end, merging it with any end already implied for this airport.</summary>
    private static void Add(Dictionary<string, List<ActiveRunway>> implied, string airportId, string designator, ActiveRunwayUse use)
    {
        string airport = NavigationDatabase.NormalizeAirport(airportId);
        if (!implied.TryGetValue(airport, out List<ActiveRunway>? ends))
        {
            ends = [];
            implied[airport] = ends;
        }

        var entry = new ActiveRunway(designator, use);
        int index = ends.FindIndex(runway => string.Equals(runway.Designator, entry.Designator, StringComparison.Ordinal));
        if (index < 0)
        {
            ends.Add(entry);
        }
        else if ((ends[index].Use != ActiveRunwayUse.Both) && (ends[index].Use != use))
        {
            ends[index] = new ActiveRunway(entry.Designator, ActiveRunwayUse.Both);
        }
    }
}
