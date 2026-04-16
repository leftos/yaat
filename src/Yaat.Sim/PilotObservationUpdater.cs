namespace Yaat.Sim;

/// <summary>
/// Per-tick evaluator for <see cref="PilotObservation"/>s. Called from the
/// simulation tick alongside <c>FlightPhysics.UpdateCommandQueue</c>; each
/// observation re-checks its condition and either resolves (pilot reports the
/// outcome via <see cref="AircraftState.PendingNotifications"/>) or stays
/// pending. Observations referencing aircraft that have left the sim are
/// silently dropped.
/// </summary>
public static class PilotObservationUpdater
{
    public static void Update(AircraftState aircraft, Func<string, AircraftState?>? aircraftLookup, WeatherProfile? weather)
    {
        if (aircraft.PendingObservations.Count == 0)
        {
            return;
        }

        var observations = aircraft.PendingObservations;
        for (int i = observations.Count - 1; i >= 0; i--)
        {
            var obs = observations[i];
            bool resolved = obs switch
            {
                TrafficAcquisitionObservation traffic => TryResolveTraffic(aircraft, traffic, aircraftLookup, weather),
                _ => false,
            };

            if (resolved)
            {
                observations.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Re-runs visual acquisition for a pending traffic observation. Returns
    /// true if the observation is done (acquired → pilot reports in sight, or
    /// target aircraft gone → silently drop) and should be removed.
    /// </summary>
    private static bool TryResolveTraffic(
        AircraftState aircraft,
        TrafficAcquisitionObservation obs,
        Func<string, AircraftState?>? aircraftLookup,
        WeatherProfile? weather
    )
    {
        if (aircraftLookup is null)
        {
            return false;
        }

        var target = aircraftLookup(obs.TargetCallsign);
        if (target is null)
        {
            // Target left the simulation. Silently drop the observation.
            return true;
        }

        var result = VisualAcquisition.TryAcquireTraffic(aircraft, target, weather);
        if (!result.Acquired)
        {
            return false;
        }

        aircraft.HasReportedTrafficInSight = true;
        aircraft.LastReportedTrafficCallsign = obs.TargetCallsign.ToUpperInvariant();
        aircraft.PendingNotifications.Add(Commands.NavigationCommandHandler.FormatTrafficInSightNotification(aircraft, obs.TargetCallsign));
        return true;
    }
}
