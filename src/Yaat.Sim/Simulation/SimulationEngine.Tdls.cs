using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation.Tdls;

namespace Yaat.Sim.Simulation;

// The vTDLS spine steps — the auto-queue sweep, the auto-WILCO scheduler, the TTL expiry and the track removal — plus
// the spawn hook that queues a departure's PDC, the ops-configuration verb and the strip/TDLS initialisation a scenario
// load runs. They decide from engine state alone (the session clock, the ARTCC's TDLS configuration, the world), so
// every run kind builds the same DCL and PDC lists; what the mutations touched is drained to the host from
// <see cref="DrainStripTdlsChangesInto"/>.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// Auto-queues a Pending TDLS item for every aircraft whose filed departure airport is
    /// served by a TDLS-configured facility and that doesn't already have an active item or
    /// a Dumped lockout. Idempotent — re-running the tick produces no duplicates. Skips when no
    /// TDLS facilities are loaded (typical for ARTCCs whose data-api payload didn't include any
    /// tdlsConfiguration blocks).
    /// </summary>
    public void TickAutoTdlsQueue()
    {
        if (Tdls.Configs.Count == 0 || Scenario is not { } scenario)
        {
            return;
        }

        var nowUtc = scenario.SimTimeUtc;
        foreach (var ac in World.GetSnapshot())
        {
            TryQueueAutoTdlsForAircraft(Tdls, ac, nowUtc);
        }
    }

    /// <summary>
    /// Per-aircraft auto-queue check, factored out of <see cref="TickAutoTdlsQueue"/> so the
    /// scenario-load + spawn path (<see cref="AfterAircraftSpawned"/>) can fire it inline
    /// without waiting for the next tick. Returns the new record if one was queued, null
    /// otherwise (no TDLS config, no filed flight plan, dumped lockout, or duplicate).
    /// <paramref name="nowUtc"/> is the caller's session clock (<see cref="SimScenarioState.SimTimeUtc"/>),
    /// which stamps the item's creation and expiry.
    /// </summary>
    internal static TdlsItemRecord? TryQueueAutoTdlsForAircraft(TdlsState tdls, AircraftState ac, DateTime nowUtc)
    {
        // A live-traffic shadow is a real departure someone else already cleared; auto-queuing it would flood
        // the PDC queue with every real flight at a TDLS airport. Manual TDLS on a shadow stays possible.
        if (tdls.Configs.Count == 0 || ac.IsShadow)
        {
            return null;
        }

        var dep = ac.FlightPlan?.Departure;
        if (string.IsNullOrEmpty(dep))
        {
            return null;
        }

        var facility = TdlsMutations.ResolveFacilityForAirport(tdls, dep);
        if (facility is null)
        {
            return null;
        }

        lock (tdls.Gate)
        {
            if (tdls.Dumped.Contains(new DumpedKey(facility, ac.Callsign)))
            {
                return null;
            }

            if (TdlsMutations.FindActiveItem(tdls, facility, ac.Callsign) is not null)
            {
                return null;
            }

            return TdlsMutations.QueuePending(tdls, facility, ac.Callsign, null, nowUtc);
        }
    }

    /// <summary>
    /// Drains any TDLS items whose scheduled auto-WILCO time has elapsed and marks them Wilco.
    /// No-op without a loaded scenario (the clock is the scenario's).
    /// </summary>
    public void TickTdlsAutoWilco()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        var tdls = Tdls;
        var now = scenario.SimTimeUtc;

        lock (tdls.Gate)
        {
            if (tdls.ScheduledWilcoAt.Count == 0)
            {
                return;
            }

            var due = tdls.ScheduledWilcoAt.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
            if (due.Count == 0)
            {
                return;
            }

            foreach (var itemId in due)
            {
                tdls.ScheduledWilcoAt.Remove(itemId);
                TdlsMutations.MarkWilco(tdls, itemId, now);
            }
        }
    }

    /// <summary>
    /// Removes TDLS items whose two-hour TTL in sim time has elapsed — the session clock is what the
    /// items were stamped from, so the TTL is two hours of the scenario, not of the wall clock. Items
    /// leave Items entirely and the schedule is purged. No Dumped lockout — TTL expiry isn't a
    /// controller-initiated removal. No-op without a loaded scenario (the clock is the scenario's).
    /// </summary>
    public void TickTdlsExpiry()
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        var tdls = Tdls;
        var now = scenario.SimTimeUtc;

        lock (tdls.Gate)
        {
            List<TdlsItemRecord>? expired = null;
            foreach (var item in tdls.Items.Values)
            {
                if (item.ExpiresUtc <= now)
                {
                    expired ??= [];
                    expired.Add(item);
                }
            }

            if (expired is null)
            {
                return;
            }

            foreach (var item in expired)
            {
                TdlsMutations.Expire(tdls, item.Id);
                tdls.ScheduledWilcoAt.Remove(item.Id);
            }
        }
    }

    /// <summary>
    /// Removes any TDLS item (Pending DCL or Sent/Wilco PDC) whose aircraft has
    /// been tracked on STARS by any controller — a non-null <c>Track.Owner</c>.
    /// Once a departure is tracked it has left the clearance-delivery workflow,
    /// so the strip clears from every vTDLS client. No Dumped lockout: this is
    /// an automatic lifecycle removal, not a controller dump. Reads live
    /// <c>Track.Owner</c> each tick so it catches every ownership source
    /// (explicit TRACK, handoff accept, auto-track).
    /// </summary>
    public void TickTdlsTrackRemoval()
    {
        var tdls = Tdls;

        lock (tdls.Gate)
        {
            List<TdlsItemRecord>? tracked = null;
            foreach (var item in tdls.Items.Values)
            {
                if (World.FindAircraft(item.AircraftId)?.Track.Owner is not null)
                {
                    tracked ??= [];
                    tracked.Add(item);
                }
            }

            if (tracked is null)
            {
                return;
            }

            foreach (var item in tracked)
            {
                TdlsMutations.Expire(tdls, item.Id);
                tdls.ScheduledWilcoAt.Remove(item.Id);
            }
        }
    }

    /// <summary>
    /// <c>TDLSOPS</c>: selects a facility's active DCL operational configuration — by id first so the vTDLS UI is
    /// unambiguous, then by name. The mutation only; <see cref="ApplyTdlsOpConfig"/> is what flags the full state.
    /// </summary>
    internal CommandResult SetTdlsOpConfig(TdlsOpsConfigCommand cmd)
    {
        if (!Tdls.Configs.TryGetValue(cmd.FacilityId, out var config) || !config.DclOpConfigsEnabled)
        {
            return new CommandResult(false, $"{cmd.FacilityId} does not use operational configurations");
        }

        var target =
            config.OpConfigs.FirstOrDefault(c => string.Equals(c.Id, cmd.Config, StringComparison.Ordinal))
            ?? config.OpConfigs.FirstOrDefault(c => string.Equals(c.Name, cmd.Config, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            var known = string.Join(", ", config.OpConfigs.Select(c => c.Name));
            return new CommandResult(false, $"Unknown operational configuration '{cmd.Config}' at {cmd.FacilityId} (have: {known})");
        }

        Tdls.ActiveOpConfigIds[cmd.FacilityId] = target.Id;
        return new CommandResult(true, target.Name);
    }

    /// <summary>
    /// <c>TDLSOPS</c> as the router's arm runs it: <see cref="SetTdlsOpConfig"/> plus the full-state flag, since no
    /// per-item message describes an ops-configuration change. Facility-scoped rather than aircraft-scoped, so it rides
    /// the global-command path alongside TIMER/PAUSE; going through a command (not an RPC) is what gets it into the
    /// action log, so a replay rebuilds clearances from the configuration that was actually active.
    /// </summary>
    internal CommandResult ApplyTdlsOpConfig(TdlsOpsConfigCommand cmd)
    {
        var result = SetTdlsOpConfig(cmd);
        if (!result.Success)
        {
            return result;
        }

        lock (Tdls.Gate)
        {
            Tdls.Changes.MarkFullState();
        }

        return new CommandResult(true, $"{cmd.FacilityId} ops config set to {result.Message}");
    }

    /// <summary>
    /// The hooks every spawn runs, whatever put the aircraft in the world: the TDLS auto-queue for a departure filed at
    /// the scenario's primary airport. Fired inline at spawn time so the DCL list populates immediately rather than on
    /// the next tick; <see cref="TickAutoTdlsQueue"/> stays as the catch-up path for flight plans edited after spawn.
    /// The broadcast is the later drain's, so a client learns the callsign before the PDC for it arrives.
    /// </summary>
    internal void AfterAircraftSpawned(AircraftState ac)
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        if (!IsDepartureAircraft(ac, scenario))
        {
            return;
        }

        var queued = TryQueueAutoTdlsForAircraft(Tdls, ac, scenario.SimTimeUtc);
        if (queued is null)
        {
            return;
        }

        _logger.LogInformation("Auto-queued PDC for {Callsign} at facility {FacilityId}", queued.AircraftId, queued.FacilityId);
    }

    /// <summary>
    /// Departure detection: aircraft whose filed departure airport matches the scenario's
    /// primary airport. Covers ground and airborne-but-just-departed cases without needing to
    /// inspect phase state. Arrivals fail this test (their destination matches the primary
    /// airport, not their departure).
    ///
    /// Uses <see cref="NavigationDatabase.AirportIdsMatch"/> so ICAO ("KOAK") filed
    /// plans match FAA ("OAK") scenario airport ids.
    /// </summary>
    public static bool IsDepartureAircraft(AircraftState ac, SimScenarioState scenario)
    {
        // A live-traffic shadow is real traffic: no auto-TDLS PDC and no auto-printed strip for it.
        return !ac.IsShadow && NavigationDatabase.AirportIdsMatch(ac.FlightPlan.Departure, scenario.PrimaryAirportId);
    }

    /// <summary>
    /// Pre-creates the empty rack slots of every strip bay the student's position can see, and registers a
    /// <see cref="TdlsConfig"/> for every facility in the loaded ARTCC that has one, so the mutations can assume both
    /// exist. Run by every path that resolves a scenario's ARTCC configuration: the server's scenario load and the
    /// replay driver once it has restored the config and the student position. A no-op without either.
    /// </summary>
    public void InitializeStripsAndTdlsFromArtcc()
    {
        if (Scenario is not { ArtccConfig: { } config })
        {
            return;
        }

        var positionCallsign = Scenario.StudentPosition?.Callsign ?? "";
        if (!string.IsNullOrEmpty(positionCallsign))
        {
            var accessible = config.GetAllAccessibleStripBays(positionCallsign);
            if (accessible.Count > 0)
            {
                Strips.InitializeFromArtcc(accessible.Select(entry => entry.Bay));
            }
        }

        if (config.Facility is not null)
        {
            Tdls.InitializeFromArtcc(config.Facility);
        }
    }

    /// <summary>
    /// Hands the host what the strip and TDLS mutations have touched since the last drain. Called by the action router
    /// after every routed action and by the post-physics spine step for what the tick steps produced; a host that
    /// broadcasts turns it into messages, a bare or replaying one drops it.
    /// </summary>
    internal void DrainStripTdlsChangesInto(ITdlsChangeConsumer host)
    {
        if (Tdls.Changes.HasAny)
        {
            host.OnTdlsChanged(Tdls.Changes.Drain());
        }
    }
}
