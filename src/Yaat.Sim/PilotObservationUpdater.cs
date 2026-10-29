using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Yaat.Sim;

/// <summary>
/// Per-tick evaluator for <see cref="PilotObservation"/>s. Called from the
/// simulation tick alongside <c>FlightPhysics.UpdateCommandQueue</c>; each
/// observation re-checks its condition and either resolves (pilot reports the
/// outcome via <see cref="AircraftState.PendingWarnings"/> for attention-
/// grabbing events like traffic acquisition) or stays pending. Observations
/// referencing aircraft that have left the sim are silently dropped.
/// </summary>
public static class PilotObservationUpdater
{
    private static readonly ILogger Log = SimLog.CreateLogger("PilotObservationUpdater");

    /// <summary>
    /// The acquisition gates already logged for a given pending observation, so a rejection that holds tick after
    /// tick is reported once rather than on every physics sub-tick. Keyed by reference: a fresh RTIS replaces the
    /// observation and starts its own log, and an observation dropped by the run releases its entry.
    /// </summary>
    private static readonly ConditionalWeakTable<TrafficAcquisitionObservation, HashSet<VisualAcquisitionFailure>> LoggedRejections = [];

    /// <summary>
    /// How long a traffic lookout stays open once issued: two minutes of sim time, after which the pilot stops looking
    /// and keys up once with "still negative contact on that traffic". A traffic advisory is about traffic passing close
    /// to the flight path now (AIM 4-1-15.a.1); traffic sighted minutes later is not the traffic the controller pointed
    /// out. FAA text sets no pilot-side limit — closing out an unsighted advisory is the controller's job (7110.65
    /// 2-1-21.a.7) — so the two-minute figure and the unsolicited report are an owner ruling for training value.
    /// </summary>
    public const double TrafficLookoutLifetimeSeconds = 120;

    /// <summary>One tick's inputs for the resolvers: the scenario clock and the mode/speech flags the pilot's lines route by.</summary>
    private readonly record struct TickContext(double SimTimeSeconds, PhysicsTickOptions Options);

    public static void Update(
        AircraftState aircraft,
        Func<string, AircraftState?>? aircraftLookup,
        WeatherProfile? weather,
        double simTimeSeconds,
        PhysicsTickOptions options
    )
    {
        if (aircraft.PendingObservations.Count == 0)
        {
            return;
        }

        TickContext tick = new(simTimeSeconds, options);
        List<PilotObservation> observations = aircraft.PendingObservations;
        for (int i = observations.Count - 1; i >= 0; i--)
        {
            PilotObservation obs = observations[i];
            bool resolved = obs switch
            {
                TrafficAcquisitionObservation traffic => TryResolveTraffic(aircraft, traffic, aircraftLookup, weather, tick),
                FieldAcquisitionObservation => TryResolveField(aircraft, weather, tick),
                _ => false,
            };

            if (resolved)
            {
                observations.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Re-runs visual acquisition for a pending traffic observation. Returns true if the observation is done, by any of
    /// its four endings: acquired → the pilot reports in sight; target aircraft gone → silently dropped; the ownship
    /// landing → "still negative contact"; the two-minute lifetime run out → the same report. Acquisition is tried
    /// before expiry so traffic first seen on the tick the lookout would end still counts as in sight.
    /// </summary>
    private static bool TryResolveTraffic(
        AircraftState aircraft,
        TrafficAcquisitionObservation obs,
        Func<string, AircraftState?>? aircraftLookup,
        WeatherProfile? weather,
        TickContext tick
    )
    {
        if (aircraftLookup is null)
        {
            return false;
        }

        AircraftState? target = aircraftLookup(obs.TargetCallsign);
        if (target is null)
        {
            // Target left the simulation. Silently drop the observation.
            return true;
        }

        if (obs.ArmedAirborne && aircraft.IsOnGround)
        {
            EndLookout(aircraft, obs, "touchdown", tick);
            return true;
        }

        VisualAcquisitionResult result = VisualAcquisition.TryAcquireTraffic(aircraft, target, weather);
        if (result.Acquired)
        {
            aircraft.Approach.HasReportedTrafficInSight = true;
            aircraft.Approach.LastReportedTrafficCallsign = obs.TargetCallsign.ToUpperInvariant();
            Log.LogDebug(
                "Traffic observation acquired: {Follower} has {Target} in sight at {Distance:F1} nm",
                aircraft.Callsign,
                obs.TargetCallsign,
                result.DistanceNm
            );
            Pilot.PilotResponder.RouteRpoSayReadback(
                aircraft,
                tick.Options.SoloTrainingMode,
                tick.Options.RpoShowPilotSpeech,
                Pilot.PilotResponder.BuildTrafficInSight(aircraft, obs.TargetCallsign)
            );
            Log.LogDebug("Traffic in sight readback queued for {Follower}, reporting {Target}", aircraft.Callsign, obs.TargetCallsign);
            return true;
        }

        if (tick.SimTimeSeconds - obs.IssuedAtSeconds >= TrafficLookoutLifetimeSeconds)
        {
            EndLookout(aircraft, obs, "expired", tick);
            return true;
        }

        LogRejectionOnce(aircraft, obs, result.Reason);
        return false;
    }

    /// <summary>
    /// Ends a lookout that ran its course or landed with the traffic still unseen, the pilot keying up once with
    /// "still negative contact on that traffic" so the controller hears the traffic was never acquired instead of being
    /// left to assume it was. <paramref name="reason"/> reports why in the log: "expired" for the two-minute lifetime,
    /// "touchdown" for the ownship reaching the ground first.
    /// </summary>
    private static void EndLookout(AircraftState aircraft, TrafficAcquisitionObservation obs, string reason, TickContext tick)
    {
        Log.LogDebug(
            "Traffic observation ended: {Follower} never acquired {Target} — {Reason} at {SimTime:F1}s",
            aircraft.Callsign,
            obs.TargetCallsign,
            reason,
            tick.SimTimeSeconds
        );
        Pilot.PilotResponder.ReportNegativeContact(
            aircraft,
            tick.Options.SoloTrainingMode,
            Pilot.PilotResponder.BuildNegativeContact(aircraft, obs.TargetCallsign)
        );
    }

    /// <summary>
    /// Logs the gate holding an acquisition back the first time it rejects a given observation, so a persistent
    /// rejection (a target behind the ownship, out of range, or under a deck) is visible in the log without
    /// drowning it: the gate is named once per observation, not once per tick.
    /// </summary>
    private static void LogRejectionOnce(AircraftState aircraft, TrafficAcquisitionObservation obs, VisualAcquisitionFailure gate)
    {
        if (!Log.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        if (LoggedRejections.GetOrCreateValue(obs).Add(gate))
        {
            Log.LogDebug("Traffic observation: {Follower} cannot yet acquire {Target} — {Gate}", aircraft.Callsign, obs.TargetCallsign, gate);
        }
    }

    /// <summary>
    /// Re-runs visual acquisition for a pending field observation. Returns
    /// true if the observation is done (acquired → pilot reports field in
    /// sight, or the destination is no longer lookupable → silently drop)
    /// and should be removed.
    /// </summary>
    private static bool TryResolveField(AircraftState aircraft, WeatherProfile? weather, TickContext tick)
    {
        VisualAcquisitionResult? result = VisualAcquisition.TryAcquireAirport(aircraft, weather);
        if (result is null)
        {
            // Destination cleared or no longer in nav database. Silently drop.
            return true;
        }

        if (!result.Value.Acquired)
        {
            return false;
        }

        aircraft.Approach.HasReportedFieldInSight = true;
        Pilot.PilotResponder.RouteRpoSayReadback(
            aircraft,
            tick.Options.SoloTrainingMode,
            tick.Options.RpoShowPilotSpeech,
            Pilot.PilotResponder.BuildFieldInSight(aircraft)
        );
        return true;
    }
}
