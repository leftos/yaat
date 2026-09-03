using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airspace;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// Shadow aircraft from the live feed -- samples, beacon tracking and runway-use latching.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// Applies a live-traffic sample to the named shadow, creating it from <paramref name="spawnState"/>
    /// when it does not exist yet, and records the action. Call from pre-physics of the current second
    /// (the server sync does) so the recorded second matches the pre-tick replay placement. Returns
    /// false when nothing changed: the sample is stale, the aircraft has been assumed, or it is unknown
    /// and no spawn state was given.
    /// </summary>
    public bool ApplyLiveTrafficSample(string callsign, LiveTrafficSample sample, AircraftSnapshotDto? spawnState)
    {
        bool spawned = false;
        var ac = World.FindAircraft(callsign);
        if (ac is null)
        {
            if (spawnState is null)
            {
                _logger.LogWarning("Live sample for unknown aircraft {Callsign} without spawn state; ignored", callsign);
                return false;
            }

            ac = SpawnShadow(spawnState);
            spawned = true;
        }

        if (!ac.IsShadow)
        {
            return false;
        }

        uint codeBefore = spawned ? 0 : ac.Transponder.Code;
        if (!spawned && !LiveTrafficKinematics.Apply(ac, sample))
        {
            return false;
        }

        double now = Scenario?.ElapsedSeconds ?? 0;
        ac.LiveTraffic!.AppliedAtSimSeconds = now;
        LiveTrafficKinematics.Resync(ac, now, World.Weather);
        if (Scenario is not null)
        {
            LiveTrafficOwnerResolver.Apply(ac, sample, Scenario);
        }

        TrackShadowBeacon(codeBefore, ac.Transponder.Code);
        RecordAction(new RecordedLiveTrafficSample(now, callsign, sample, spawned ? spawnState : null));
        return true;
    }

    /// <summary>
    /// A shadow squawks whatever the feed reports, so the pool must learn each code it adopts (a simulated
    /// aircraft must never be issued a real one) and forget the code it gave up — on spawn, on every change,
    /// and on removal.
    /// </summary>
    private void TrackShadowBeacon(uint before, uint after)
    {
        if (before == after)
        {
            return;
        }

        if (before != 0)
        {
            BeaconCodePool.Release(before);
        }

        if (after != 0)
        {
            BeaconCodePool.MarkUsed(after);
        }
    }

    /// <summary>
    /// Per-second runway-use observer for shadows on the room's primary airport (else the shadow's destination / departure
    /// airport, so a satellite-field landing is seen too): the edge from airborne
    /// <see cref="RunwayUseKind.Landing"/> to <see cref="RunwayUseKind.OnSurface"/> is a landing, stamped
    /// <see cref="CompletionReason.Landed"/> so the later feed removal records a completion. Called from
    /// <see cref="TickPostPhysics"/> AND the live server's post-physics step — add it to both when moving it.
    /// </summary>
    public void TickLiveTrafficRunwayUse()
    {
        var runways = RunwayOccupancy.AirportRunways(Scenario?.PrimaryAirportId);
        var layout = World.GroundLayout;
        foreach (var ac in World.GetSnapshot())
        {
            if (ac.LiveTraffic is not { } lt)
            {
                continue;
            }

            var use =
                RunwayOccupancy.ClassifyBest(ac, runways, layout)
                ?? RunwayOccupancy.ClassifyBest(ac, RunwayOccupancy.AirportRunways(ac.FlightPlan.Destination), layout)
                ?? RunwayOccupancy.ClassifyBest(ac, RunwayOccupancy.AirportRunways(ac.FlightPlan.Departure), layout);
            var kind = use?.Kind;
            if (!ac.IsOnGround || kind is null)
            {
                // Airborne again, or off the pavement: the next takeoff roll from this runway is a real one.
                lt.LandedOnRunway = false;
            }

            bool touchedDown =
                lt.LastRunwayUse == RunwayUseKind.Landing && ac.IsOnGround && kind is RunwayUseKind.OnSurface or RunwayUseKind.Departing;
            if (touchedDown)
            {
                lt.LandedOnRunway = true;
                kind = RunwayUseKind.OnSurface;
                if (ac.CompletionReason == CompletionReason.Active)
                {
                    ac.CompletionReason = CompletionReason.Landed;
                    ac.CompletedAtSeconds = Scenario?.ElapsedSeconds;
                    _logger.LogInformation("{Callsign} (live) landed at {Airport}", ac.Callsign, Scenario?.PrimaryAirportId);
                }
            }

            if (lt.LastRunwayUse == RunwayUseKind.Departing && !ac.IsOnGround)
            {
                lt.DepartedOnRunway = true;
            }
            else if (lt.DepartedOnRunway && (ac.IsOnGround || !StillInDepartureWindow(ac, lt)))
            {
                lt.DepartedOnRunway = false;
            }

            if (use is not null)
            {
                lt.LatchedRunwayAirport = use.Runway.AirportId;
                lt.LatchedRunwayDesignator = use.Runway.Designator;
            }

            lt.LastRunwayUse = kind;
        }
    }

    /// <summary>Departure window for the latch: within a mile of the latched runway's departure end (the §3-9-6 landmarks all lie inside it).</summary>
    private const double DepartureLatchWindowNm = 1.0;

    private static bool StillInDepartureWindow(AircraftState ac, AircraftLiveTraffic lt)
    {
        if (LiveTrafficLatchedRunway(lt) is not { } runway)
        {
            return false;
        }

        return GeoMath.DistanceNm(ac.Position, new LatLon(runway.EndLatitude, runway.EndLongitude)) <= DepartureLatchWindowNm;
    }

    /// <summary>The runway the observer latched for a shadow, oriented to the latched designator; null when none.</summary>
    public static RunwayInfo? LiveTrafficLatchedRunway(AircraftLiveTraffic lt)
    {
        if (lt.LatchedRunwayAirport is null || lt.LatchedRunwayDesignator is null)
        {
            return null;
        }

        return RunwayOccupancy
            .AirportRunways(lt.LatchedRunwayAirport)
            .FirstOrDefault(r => r.Id.Contains(lt.LatchedRunwayDesignator))
            ?.ForApproach(lt.LatchedRunwayDesignator);
    }

    /// <summary>Removes a shadow (never an assumed aircraft) and records the removal. Not a completion.</summary>
    public bool RemoveLiveTraffic(string callsign, LiveTrafficRemovalReason reason)
    {
        var ac = World.FindAircraft(callsign);
        if (ac is null || !ac.IsShadow)
        {
            return false;
        }

        World.RemoveAircraft(callsign);
        TrackShadowBeacon(ac.Transponder.Code, 0);
        RecordAction(new RecordedLiveTrafficRemoval(Scenario?.ElapsedSeconds ?? 0, callsign, reason));
        return true;
    }

    /// <summary>
    /// Replay twin of <see cref="ApplyLiveTrafficSample"/> (no recording). Public so the server brain's
    /// reconstruction and tape playback apply the same action the same way — including the
    /// <see cref="LiveTrafficKinematics.Resync"/> that ages the sample to the replayed second.
    /// </summary>
    public void ApplyRecordedLiveTrafficSample(RecordedLiveTrafficSample recorded)
    {
        var ac = World.FindAircraft(recorded.Callsign);
        if (ac is null)
        {
            if (recorded.SpawnState is null)
            {
                _logger.LogWarning("Replayed live sample for unknown aircraft {Callsign} without spawn state; ignored", recorded.Callsign);
                return;
            }

            ac = SpawnShadow(recorded.SpawnState);
            TrackShadowBeacon(0, ac.Transponder.Code);
        }
        else
        {
            if (!ac.IsShadow)
            {
                return;
            }

            uint codeBefore = ac.Transponder.Code;
            if (!LiveTrafficKinematics.Apply(ac, recorded.Sample))
            {
                return;
            }

            TrackShadowBeacon(codeBefore, ac.Transponder.Code);
        }

        ac.LiveTraffic!.AppliedAtSimSeconds = Scenario?.ElapsedSeconds ?? 0;
        LiveTrafficKinematics.Resync(ac, Scenario?.ElapsedSeconds ?? 0, World.Weather);
        if (Scenario is not null)
        {
            LiveTrafficOwnerResolver.Apply(ac, recorded.Sample, Scenario);
        }
    }

    /// <summary>Replay twin of <see cref="RemoveLiveTraffic"/> (no recording); public for the server brain.</summary>
    public void ApplyRecordedLiveTrafficRemoval(RecordedLiveTrafficRemoval recorded)
    {
        var ac = World.FindAircraft(recorded.Callsign);
        if (ac is { IsShadow: true })
        {
            World.RemoveAircraft(recorded.Callsign);
            TrackShadowBeacon(ac.Transponder.Code, 0);
        }
    }

    private AircraftState SpawnShadow(AircraftSnapshotDto spawnState)
    {
        var state = AircraftState.FromSnapshot(spawnState, null);
        World.AddAircraft(state);
        return state;
    }
}
