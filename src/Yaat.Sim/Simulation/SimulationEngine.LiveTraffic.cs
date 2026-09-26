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
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Spine;
using Yaat.Sim.Training;

namespace Yaat.Sim.Simulation;

// Shadow aircraft from the live feed -- samples, beacon tracking and runway-use latching.
public sealed partial class SimulationEngine
{
    /// <summary>
    /// Applies a live-traffic sample to the named shadow, creating it from <paramref name="spawnState"/>
    /// when it does not exist yet, and records the action. Call from pre-physics of the current second
    /// (<see cref="TickLiveTrafficSync"/> does) so the recorded second matches the pre-tick replay placement. Returns
    /// false when nothing changed: the sample is stale, the aircraft has been assumed, or it is unknown
    /// and no spawn state was given.
    /// </summary>
    public bool ApplyLiveTrafficSample(string callsign, LiveTrafficSample sample, AircraftSnapshotDto? spawnState)
    {
        bool spawned = false;
        AircraftState? ac = World.FindAircraft(callsign);
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
        IReadOnlyList<RunwayInfo> runways = RunwayOccupancy.AirportRunways(Scenario?.PrimaryAirportId);
        AirportGroundLayout? layout = World.GroundLayout;
        foreach (AircraftState ac in World.GetSnapshot())
        {
            if (ac.LiveTraffic is not { } lt)
            {
                continue;
            }

            RunwayUse? use =
                RunwayOccupancy.ClassifyBest(ac, runways, layout)
                ?? RunwayOccupancy.ClassifyBest(ac, RunwayOccupancy.AirportRunways(ac.FlightPlan.Destination), layout)
                ?? RunwayOccupancy.ClassifyBest(ac, RunwayOccupancy.AirportRunways(ac.FlightPlan.Departure), layout);
            RunwayUseKind? kind = use?.Kind;
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

    /// <summary>
    /// <c>DEL</c> on a live-traffic shadow: removes the shadow with a recorded
    /// <see cref="LiveTrafficRemovalReason.Deleted"/> removal — the record a replay hides it again from — then hides the
    /// callsign from the feed and drops any queued spawn under it (<see cref="HideDeletedLiveTraffic"/>). What the room
    /// does with the removal is the caller's to hand to its host.
    /// </summary>
    public void HideLiveTraffic(string callsign)
    {
        RemoveLiveTraffic(callsign, LiveTrafficRemovalReason.Deleted);
        HideDeletedLiveTraffic(callsign);
    }

    /// <summary>
    /// What a <c>DEL</c> on a shadow leaves behind, live and on replay alike: the callsign in
    /// <see cref="SimScenarioState.SuppressedLiveTraffic"/>, and no queued spawn under it — so a replayed <c>DEL</c> text
    /// always refuses at the aircraft-exists guard.
    /// </summary>
    private void HideDeletedLiveTraffic(string callsign)
    {
        Scenario?.SuppressedLiveTraffic.Add(callsign);
        Scenario?.DelayedQueue.RemoveAll(e => e.Aircraft.State.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Removes a shadow (never an assumed aircraft) and records the removal. Not a completion.</summary>
    public bool RemoveLiveTraffic(string callsign, LiveTrafficRemovalReason reason)
    {
        AircraftState? ac = World.FindAircraft(callsign);
        if (ac is null || !ac.IsShadow)
        {
            return false;
        }

        RegisterDisconnectCoast(ac);
        World.RemoveAircraft(callsign);
        TrackShadowBeacon(ac.Transponder.Code, 0);
        RecordAction(new RecordedLiveTrafficRemoval(Scenario?.ElapsedSeconds ?? 0, callsign, reason));
        return true;
    }

    /// <summary>
    /// Seconds of shadow silence before a track the feed still delivers — just outside the room's scope — is removed.
    /// Much shorter than the silence backstop: the feed said where the aircraft is, and it is not here.
    /// </summary>
    public const double LiveTrafficOutOfScopeRemovalSeconds = 15;

    /// <summary>
    /// The pre-physics live-traffic sync, last in pre-physics so a sample placed at second <c>t</c> is recorded at
    /// <c>t</c> and replays pre-tick at <c>t</c>. Asks <paramref name="port"/> what is in scope this second; clears every
    /// shadow first when the port says so; spawns shadows for new tracks and feeds existing ones fresh samples; then
    /// ages out the shadows the feed no longer supports. Every sample and removal goes through
    /// <see cref="ApplyLiveTrafficSample"/> / <see cref="RemoveLiveTraffic"/>, so the recording is the one the replay
    /// twins read; what the room does with a spawn, a removal, a collision or a filter sweep is the host's, told
    /// through <paramref name="host"/>. A host with no feed supplies <see cref="EmptyLiveTrafficFeedPort"/>, whose
    /// every second is inert. Every second live traffic is off forgets the callsigns the instructor hid
    /// (<see cref="SimScenarioState.SuppressedLiveTraffic"/>), on every run kind, so turning it off and on brings them back.
    /// </summary>
    public void TickLiveTrafficSync(ILiveTrafficFeedPort port, IHostConsumers host)
    {
        if (Scenario is not { } scenario)
        {
            return;
        }

        if (!scenario.LiveTrafficEnabled)
        {
            scenario.SuppressedLiveTraffic.Clear();
        }

        LiveTrafficFeedSecond feed = port.BeginSecond();
        if (feed.ClearShadows is { } clearReason)
        {
            ClearLiveTrafficShadows(clearReason, host);
        }

        if (!feed.Syncs)
        {
            return;
        }

        foreach (LiveTrafficFeedTrack track in feed.Tracks)
        {
            if (!track.MatchesFilter)
            {
                // The room's filter excludes it: never spawned, and an existing shadow is torn down below.
                continue;
            }

            ApplyLiveTrafficTrack(track, host, scenario);
        }

        RemoveAbsentLiveTraffic(port, scenario.ElapsedSeconds, host);
        port.EndSecond();
    }

    /// <summary>
    /// Removes every shadow with <paramref name="reason"/>. A re-acquire after a gap that removed any tells the host how
    /// many, so the instructor learns how far the picture moved.
    /// </summary>
    private void ClearLiveTrafficShadows(LiveTrafficRemovalReason reason, IHostConsumers host)
    {
        int removed = 0;
        foreach (AircraftState ac in World.GetSnapshot())
        {
            if (ac.IsShadow && RemoveLiveTrafficShadow(ac, reason, host))
            {
                removed++;
            }
        }

        if ((reason == LiveTrafficRemovalReason.Reanchored) && (removed > 0))
        {
            host.OnLiveTrafficReacquired(removed);
        }
    }

    private void ApplyLiveTrafficTrack(LiveTrafficFeedTrack track, IHostConsumers host, SimScenarioState scenario)
    {
        string callsign = track.Callsign;
        AircraftState? existing = World.FindAircraft(callsign);
        if (existing is not null && !existing.IsShadow)
        {
            // A simulated aircraft owns the callsign: the feed is ignored for it. An *assumed* one is not a collision
            // — the controller took this very track, which any command on a shadow now does — so it is skipped in
            // silence; warning would put a line on the terminal for every routine hand-off.
            if (!existing.AssumedFromLiveTraffic)
            {
                host.OnLiveTrafficCallsignInUse(callsign);
            }

            return;
        }

        if (scenario.SuppressedLiveTraffic.Contains(callsign))
        {
            return;
        }

        if (existing is not null)
        {
            ApplyLiveTrafficSample(callsign, track.Sample, null);
            return;
        }

        if (ApplyLiveTrafficSample(callsign, track.Sample, track.SpawnState()))
        {
            host.OnLiveTrafficSpawned(World.FindAircraft(callsign)!, track.Sample.Source);
        }
    }

    /// <summary>
    /// Tiered teardown, explicit lifecycle first. A shadow whose feed row is ended was ended by the feed itself and is
    /// removed promptly — a landed arrival must not dead-reckon down the runway for a silence window. One the feed has
    /// filtered out goes at once, reported in one line. One the feed still delivers but outside the room's scope goes at
    /// <see cref="LiveTrafficOutOfScopeRemovalSeconds"/>. Only then the silence backstop
    /// (<see cref="LiveTrafficKinematics.RemovalAfterSeconds"/>) — SCDS publishes selectively, so silence alone is weak
    /// evidence and the window is generous; a feed repeating the same view still lands here (repeats are never newer
    /// than the applied sample, so they refresh nothing).
    /// </summary>
    private void RemoveAbsentLiveTraffic(ILiveTrafficFeedPort port, double elapsed, IHostConsumers host)
    {
        int filteredOut = 0;
        foreach (AircraftState ac in World.GetSnapshot())
        {
            if (ac.LiveTraffic is not { } lt)
            {
                continue;
            }

            double silence = elapsed - lt.AppliedAtSimSeconds;
            if (silence < 2)
            {
                continue;
            }

            if (AbsentShadowRemoval(port.ShadowStatus(ac.Callsign), silence, lt.Source) is not { } reason)
            {
                continue;
            }

            if (RemoveLiveTrafficShadow(ac, reason, host) && (reason == LiveTrafficRemovalReason.Filtered))
            {
                filteredOut++;
            }
        }

        if (filteredOut > 0)
        {
            // One report, not one per callsign: a tightened filter can hide dozens at once, and the instructor
            // (who may just have advised traffic on one of them) needs to know they left by filter, not by radar.
            host.OnLiveTrafficFilteredOut(filteredOut);
        }
    }

    /// <summary>The removal tier a silent shadow falls into this second; null while it stays.</summary>
    private static LiveTrafficRemovalReason? AbsentShadowRemoval(LiveTrafficShadowStatus status, double silence, LiveTrafficSource source)
    {
        bool pastBackstop = silence > LiveTrafficKinematics.RemovalAfterSeconds(source);
        return status switch
        {
            // Absence is not an ended track: a freshly opened DVR replay's private store starts empty, and the live
            // store only forgets a row at reap — the silence backstop covers both long before that.
            LiveTrafficShadowStatus.Absent => pastBackstop ? LiveTrafficRemovalReason.Dropped : null,
            LiveTrafficShadowStatus.Ended => LiveTrafficRemovalReason.Dropped,
            LiveTrafficShadowStatus.FilteredOut => LiveTrafficRemovalReason.Filtered,
            _ when silence <= LiveTrafficOutOfScopeRemovalSeconds => null,
            LiveTrafficShadowStatus.OutOfScope => LiveTrafficRemovalReason.OutOfScope,
            _ => pastBackstop ? LiveTrafficRemovalReason.Stale : null,
        };
    }

    /// <summary>Removes one shadow and, when it was one, hands its last state to the host for the room's teardown.</summary>
    private bool RemoveLiveTrafficShadow(AircraftState ac, LiveTrafficRemovalReason reason, IHostConsumers host)
    {
        if (!RemoveLiveTraffic(ac.Callsign, reason))
        {
            return false;
        }

        host.OnLiveTrafficRemoved(ac, reason);
        return true;
    }

    /// <summary>
    /// Replay twin of <see cref="ApplyLiveTrafficSample"/> (no recording). Public so the server brain's
    /// reconstruction and tape playback apply the same action the same way — including the
    /// <see cref="LiveTrafficKinematics.Resync"/> that ages the sample to the replayed second.
    /// </summary>
    public void ApplyRecordedLiveTrafficSample(RecordedLiveTrafficSample recorded)
    {
        AircraftState? ac = World.FindAircraft(recorded.Callsign);
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

    /// <summary>
    /// Replay twin of <see cref="RemoveLiveTraffic"/> (no recording), reached from
    /// <see cref="ActionRouter.ApplyRecorded(RecordedAction, IActionHost)"/> — its only caller. A removal the
    /// instructor's <c>DEL</c> produced (<see cref="LiveTrafficRemovalReason.Deleted"/>) also hides the callsign again
    /// (<see cref="SimScenarioState.SuppressedLiveTraffic"/>), whether or not the shadow was still there to remove: the replayed
    /// <c>DEL</c> text cannot, because this record removes the shadow first and the command then refuses at the
    /// aircraft-exists guard. A removal the feed produced hides nothing — that shadow is meant to come back when the feed
    /// re-supplies it.
    /// </summary>
    public void ApplyRecordedLiveTrafficRemoval(RecordedLiveTrafficRemoval recorded)
    {
        AircraftState? ac = World.FindAircraft(recorded.Callsign);
        if (ac is { IsShadow: true })
        {
            RegisterDisconnectCoast(ac);
            World.RemoveAircraft(recorded.Callsign);
            TrackShadowBeacon(ac.Transponder.Code, 0);
        }

        if (recorded.Reason == LiveTrafficRemovalReason.Deleted)
        {
            HideDeletedLiveTraffic(recorded.Callsign);
        }
    }

    /// <summary>
    /// Puts a shadow in the world, live and on replay alike. A live shadow spawn never reaches
    /// <see cref="AfterAircraftSpawned"/>, so the disconnect-coast clear a re-supplied callsign owes happens here.
    /// </summary>
    private AircraftState SpawnShadow(AircraftSnapshotDto spawnState)
    {
        var state = AircraftState.FromSnapshot(spawnState, null);
        if (Scenario is { } scenario)
        {
            ClearDisconnectCoast(scenario, state.Callsign);
        }

        World.AddAircraft(state);
        return state;
    }
}
