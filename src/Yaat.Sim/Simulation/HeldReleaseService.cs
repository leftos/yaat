using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Pilot;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Simulation;

/// <summary>
/// One held departure as shown in the rundown / used to pick the next-pending release at a field.
/// </summary>
/// <param name="Callsign">Aircraft callsign.</param>
/// <param name="Airport">Departure airport (as stored on the flight plan).</param>
/// <param name="AircraftType">ICAO type.</param>
/// <param name="Destination">Destination airport.</param>
/// <param name="IsGroundDeparture">True = spawned ground departure holding short; false = held runway/airborne spawn.</param>
/// <param name="PendingSinceSeconds">Elapsed-seconds sort key (spawn time for ground departures, scheduled spawn time for held spawns); smaller = released first.</param>
/// <param name="Status">Human-readable hold status for the rundown.</param>
public sealed record HeldDeparture(
    string Callsign,
    string Airport,
    string AircraftType,
    string Destination,
    bool IsGroundDeparture,
    double PendingSinceSeconds,
    string Status
);

/// <summary>Result of arming/disarming/releasing — the server projects these into terminal/log lines.</summary>
public sealed record HeldReleaseResult(bool Success, string Message)
{
    /// <summary>
    /// Airborne spawn jitter (s) drawn for an immediate single release of a held runway/airborne
    /// departure. The server bakes this onto the recorded <c>REL</c> command so replay reproduces the
    /// spawn time without consuming RNG. Null for ground releases, auto-spaced (queued) releases, and
    /// failures.
    /// </summary>
    public int? SpawnJitterSeconds { get; init; }
}

/// <summary>
/// Hold for release: arms and disarms an airport, releases a departure, and builds the per-airport rundown. Releasing a
/// held ground departure clears its hold, starts its automatic-takeoff clock, and answers the release request a runway
/// spawn asked the controller for, acknowledging it on the frequency. Hold-for-release state lives in
/// <see cref="SimScenarioState"/> and on <see cref="AircraftGroundOps"/>; the server owns broadcasting the resulting rundown.
/// </summary>
public static class HeldReleaseService
{
    private static readonly ILogger Log = SimLog.CreateLogger("HeldReleaseService");

    /// <summary>
    /// A command that puts a departure onto its runway (CTO, CTOPP, LUAW): the set a hold for release withholds until the
    /// departure is released.
    /// </summary>
    public static bool IsRunwayEntryCommand(ParsedCommand command) =>
        command is ClearedForTakeoffCommand or ClearedTakeoffPresentCommand or LineUpAndWaitCommand;

    /// <summary>Minimum delay (s) from release to a held runway/airborne spawn appearing airborne.</summary>
    public const double MinSpawnReleaseDelaySeconds = 20.0;

    /// <summary>Maximum delay (s) from release to a held runway/airborne spawn appearing airborne.</summary>
    public const double MaxSpawnReleaseDelaySeconds = 60.0;

    /// <summary>Minimum tower-readback jitter (s) before a released ground departure is auto-cleared for takeoff.</summary>
    public const uint MinGroundReleaseAutoCtoJitterSeconds = 5;

    /// <summary>Range (s) of the released-ground-departure auto-CTO jitter (5..20 s with the minimum above).</summary>
    public const uint GroundReleaseAutoCtoJitterRangeSeconds = 16;

    /// <summary>Arm an airport for hold-for-release and hold any on-ground IFR departures already there.</summary>
    public static HeldReleaseResult Arm(SimScenarioState scenario, SimulationWorld world, string airport)
    {
        string normalized = airport.Trim().ToUpperInvariant();
        if (normalized.Length == 0)
        {
            return new HeldReleaseResult(false, "HFR requires an airport");
        }

        scenario.HeldDepartureAirports.Add(normalized);

        int swept = 0;
        foreach (AircraftState ac in world.GetSnapshot())
        {
            if (!ac.Ground.HeldForRelease && IsHoldableGroundDeparture(ac) && AirportMatches(DepartureAirportOf(ac), normalized))
            {
                ac.Ground.HeldForRelease = true;
                ac.Ground.ReleasedForDeparture = false;
                swept++;
            }
        }

        string suffix = swept > 0 ? $" ({swept} departure{(swept == 1 ? "" : "s")} now holding)" : "";
        return new HeldReleaseResult(true, $"Hold for release armed at {normalized}{suffix}");
    }

    /// <summary>Gap (s) the keep-spacing <c>HFROFF</c> never squeezes successive departures under.</summary>
    public const int MinimumSpacingSeconds = 120;

    /// <summary>
    /// Disarm an airport (<c>HFROFF</c>); releases the held ground departures there at once and reschedules its queued held
    /// spawns. The held departure at the bar (or lined up) is released now, and the queued spawns already due are released
    /// through the spawn gate with new times: with no <paramref name="intervalSeconds"/> each moves later by the hold's length
    /// (keep spacing), with one they follow one every interval (catch up). A queued spawn not yet due keeps its hold candidacy
    /// and moves only as far as the gap before it requires (<see cref="MinimumSpacingSeconds"/>, or the interval). No RNG.
    /// "(N released)" counts only the departures this call released.
    /// </summary>
    public static HeldReleaseResult Disarm(SimScenarioState scenario, SimulationWorld world, string airport, int? intervalSeconds)
    {
        string normalized = airport.Trim().ToUpperInvariant();
        if (!scenario.HeldDepartureAirports.Remove(normalized))
        {
            return new HeldReleaseResult(false, $"{normalized} is not armed for hold for release");
        }

        // The held departure waiting at the bar anchors the schedule: its hold began when it started holding there.
        double? atTheBarSince = HeldAtTheRunway(world, normalized) is { } atTheBar ? HoldBegan(atTheBar, scenario.ElapsedSeconds) : null;

        // Released ground departures: clear the flag and authorize roll.
        int released = 0;
        foreach (AircraftState ac in world.GetSnapshot())
        {
            if (ac.Ground.HeldForRelease && AirportMatches(DepartureAirportOf(ac), normalized))
            {
                ReleaseHeldGroundDeparture(world, ac, scenario.ElapsedSeconds);
                released++;
            }
        }

        released += RescheduleHeldSpawns(scenario, normalized, atTheBarSince, intervalSeconds);

        // Drop any pending scheduled releases for the field.
        scenario.ReleaseQueue.RemoveAll(r => AirportMatches(r.Airport, normalized));

        string suffix = released > 0 ? $" ({released} released)" : "";
        return new HeldReleaseResult(true, $"Hold for release disarmed at {normalized}{suffix}");
    }

    /// <summary>
    /// When <paramref name="ac"/>, held at the bar or lined up, began holding there: now minus the time its current phase has
    /// run, and never before it spawned (the phase clock starts counting a tick off the spawn).
    /// </summary>
    private static double HoldBegan(AircraftState ac, double now) =>
        Math.Max(ac.SpawnedAtSeconds, now - (ac.Phases?.CurrentPhase?.ElapsedSeconds ?? 0));

    /// <summary>
    /// Rewrites the spawn times of <paramref name="airport"/>'s queued held spawns at an <c>HFROFF</c> at R (the elapsed time).
    /// The released set is the held departure at the bar (original time <paramref name="atTheBarSince"/>, released at R) then
    /// the queued ones already due; the later set is the queued ones not yet due; each in (scheduled time, queue order). Keep
    /// spacing shifts each due one by D = R minus when the hold began (the bar aircraft began holding, else the first due one's
    /// time), never to before R; catch up gives the k-th released one R + k * interval. Each later one gets max(its time, the previous new time + gap). The due ones are released through
    /// the spawn gate as REL would (a runway spawn then never asks for its release); the later ones keep their candidacy.
    /// Returns the number released.
    /// </summary>
    private static int RescheduleHeldSpawns(SimScenarioState scenario, string airport, double? atTheBarSince, int? intervalSeconds)
    {
        double now = scenario.ElapsedSeconds;
        List<DelayedSpawn> candidates = HeldCandidatesInOrder(scenario, airport);
        List<DelayedSpawn> due = candidates.FindAll(e => e.SpawnAtSeconds <= now);
        List<DelayedSpawn> later = candidates.FindAll(e => e.SpawnAtSeconds > now);
        double? previous = ReleaseDueSpawns(due, now, atTheBarSince, intervalSeconds);
        if (previous is not { } prior)
        {
            return due.Count;
        }

        int gap = intervalSeconds ?? MinimumSpacingSeconds;
        int last = (int)Math.Round(prior);
        foreach (DelayedSpawn entry in later)
        {
            entry.SpawnAtSeconds = Math.Max(entry.SpawnAtSeconds, last + gap);
            last = entry.SpawnAtSeconds;
        }

        return due.Count;
    }

    /// <summary>
    /// Gives each due spawn its new time and releases it through the spawn gate; returns the last new time of the released set
    /// (R itself when only the departure at the bar was released), or null when nothing was released.
    /// </summary>
    private static double? ReleaseDueSpawns(List<DelayedSpawn> due, double now, double? atTheBarSince, int? intervalSeconds)
    {
        double? previous = atTheBarSince is null ? null : now;
        double? firstQueuedOriginal = due.Count > 0 ? due[0].SpawnAtSeconds : null;
        double holdBegan = atTheBarSince ?? firstQueuedOriginal ?? now;
        double shift = now - holdBegan;
        int k = atTheBarSince is null ? 0 : 1;
        foreach (DelayedSpawn entry in due)
        {
            double newTime = intervalSeconds is { } interval ? now + (k * interval) : entry.SpawnAtSeconds + shift;
            // Never before the HFROFF itself: an entry due before the hold began would otherwise land in the past.
            entry.SpawnAtSeconds = (int)Math.Round(Math.Max(newTime, now));
            entry.HeldForRelease = false;
            entry.Aircraft.State.Ground.ReleasedAtSpawnGate = true;
            previous = entry.SpawnAtSeconds;
            k++;
        }

        return previous;
    }

    /// <summary>
    /// Entry point for <c>REL</c>/<c>CTOA</c>. <paramref name="target"/> is a callsign (release that
    /// specific departure) or an airport (release the next-pending there, or — when
    /// <paramref name="intervalSeconds"/> is set — the whole field's queue auto-spaced).
    /// </summary>
    public static HeldReleaseResult Release(
        SimScenarioState scenario,
        SimulationWorld world,
        SerializableRandom rng,
        string target,
        int? intervalSeconds
    ) => ReleaseCore(scenario, world, rng, bakedJitter: null, target, intervalSeconds);

    /// <summary>
    /// Replay entry for <c>REL</c>. Reproduces a live release deterministically: the immediate
    /// airborne path uses <paramref name="bakedJitterSeconds"/> (the value drawn live and baked onto
    /// the recorded command) instead of sampling, so no RNG is consumed and the shared
    /// <see cref="SimulationWorld.Rng"/> stream stays aligned with the original run. A legacy recording
    /// with no baked jitter falls back to <see cref="MinSpawnReleaseDelaySeconds"/> — still
    /// deterministic and RNG-free (the spawn itself replays from a recorded spawn action).
    /// </summary>
    public static HeldReleaseResult ReplayRelease(
        SimScenarioState scenario,
        SimulationWorld world,
        string target,
        int? intervalSeconds,
        int? bakedJitterSeconds
    ) => ReleaseCore(scenario, world, rng: null, bakedJitter: bakedJitterSeconds ?? (int)MinSpawnReleaseDelaySeconds, target, intervalSeconds);

    /// <summary>
    /// Shared body for <see cref="Release"/> (live, <paramref name="rng"/> drives the airborne jitter)
    /// and <see cref="ReplayRelease"/> (replay, <paramref name="bakedJitter"/> drives it). Exactly one
    /// of the two is non-null on the immediate-airborne path.
    /// </summary>
    private static HeldReleaseResult ReleaseCore(
        SimScenarioState scenario,
        SimulationWorld world,
        SerializableRandom? rng,
        int? bakedJitter,
        string target,
        int? intervalSeconds
    )
    {
        string normalized = target.Trim().ToUpperInvariant();

        // Callsign form: release that specific held departure.
        if (FindHeldByCallsign(scenario, world, normalized) is { } held)
        {
            return ReleaseOneCore(scenario, world, rng, bakedJitter, held);
        }

        // Airport form.
        var atField = BuildRundown(scenario, world).Where(h => AirportMatches(h.Airport, normalized)).ToList();
        if (atField.Count == 0)
        {
            return new HeldReleaseResult(false, $"No departures held for release at {normalized}");
        }

        if (intervalSeconds is not { } interval || atField.Count == 1)
        {
            return ReleaseOneCore(scenario, world, rng, bakedJitter, atField[0]);
        }

        // Auto-spaced release of the whole field's queue: schedule each in pending order. No jitter is
        // drawn here — each scheduled release fires later from the tick loop (ProcessReleaseQueue),
        // which draws its airborne jitter from the snapshot-restored shared RNG and so reproduces on
        // replay without baking.
        for (int i = 0; i < atField.Count; i++)
        {
            scenario.ReleaseQueue.Add(
                new ScheduledRelease
                {
                    Airport = normalized,
                    Callsign = atField[i].Callsign,
                    FireAtSeconds = scenario.ElapsedSeconds + (i * interval),
                }
            );
        }

        double minutes = interval / 60.0;
        return new HeldReleaseResult(true, $"Releasing {atField.Count} from {normalized}, {minutes:0.#} min apart");
    }

    /// <summary>
    /// Release a single held departure (used by manual single release and by
    /// <c>ProcessReleaseQueue</c>). For a held runway/airborne spawn, schedules its spawn after a
    /// randomized airborne delay; for a held ground departure, clears the hold so the holding-short
    /// auto-CTO fires.
    /// </summary>
    public static HeldReleaseResult ReleaseOne(SimScenarioState scenario, SimulationWorld world, SerializableRandom rng, HeldDeparture held) =>
        ReleaseOneCore(scenario, world, rng, bakedJitter: null, held);

    private static HeldReleaseResult ReleaseOneCore(
        SimScenarioState scenario,
        SimulationWorld world,
        SerializableRandom? rng,
        int? bakedJitter,
        HeldDeparture held
    )
    {
        if (held.IsGroundDeparture)
        {
            AircraftState? ac = world.FindAircraft(held.Callsign);
            if (ac is null || !ac.Ground.HeldForRelease)
            {
                return new HeldReleaseResult(false, $"{held.Callsign} is no longer held");
            }

            ReleaseHeldGroundDeparture(world, ac, scenario.ElapsedSeconds);
            return new HeldReleaseResult(true, $"{held.Callsign} released");
        }

        DelayedSpawn? entry = scenario.DelayedQueue.FirstOrDefault(d =>
            d.HeldForRelease && d.Aircraft.State.Callsign.Equals(held.Callsign, StringComparison.OrdinalIgnoreCase)
        );
        if (entry is null)
        {
            return new HeldReleaseResult(false, $"{held.Callsign} is no longer held");
        }

        entry.HeldForRelease = false;
        // Released through the spawn gate, the departure already has its release: a runway spawn must not ask for it again,
        // and departs as one that asked and got REL.
        entry.Aircraft.State.Ground.ReleasedAtSpawnGate = true;
        int jitter =
            bakedJitter
            ?? rng?.Next((int)MinSpawnReleaseDelaySeconds, (int)MaxSpawnReleaseDelaySeconds + 1)
            ?? throw new InvalidOperationException("ReleaseOneCore requires either a baked jitter or an RNG to sample.");
        entry.SpawnAtSeconds = (int)scenario.ElapsedSeconds + jitter;
        return new HeldReleaseResult(true, $"{held.Callsign} released") { SpawnJitterSeconds = jitter };
    }

    /// <summary>
    /// Releases a held ground departure (<c>REL</c>, or <c>HFROFF</c> at its field): clears the hold, starts the
    /// auto-takeoff clock, and answers the open <see cref="PilotPendingRequestKind.Release"/> request of a runway spawn that
    /// asked for its release, so it stops following up, the awaiting-controller-response gate clears, and it departs on the
    /// line-up-and-wait auto-clearance.
    /// </summary>
    private static void ReleaseHeldGroundDeparture(SimulationWorld world, AircraftState aircraft, double elapsedSeconds)
    {
        aircraft.Ground.HeldForRelease = false;
        aircraft.Ground.ReleasedForDeparture = true;
        aircraft.Ground.ReleasedAtSeconds = elapsedSeconds;
        // A lined-up runway spawn with no preset held at spawn (its runway has no bar) leaves as one released through the
        // spawn gate: it never asks for the release it now has, and departs as such a spawn does.
        if ((aircraft.Phases?.CurrentPhase is LinedUpAndWaitingPhase) && (aircraft.Ground.InitialCallup == InitialCallupPlan.RunwayNoPreset))
        {
            aircraft.Ground.ReleasedAtSpawnGate = true;
        }

        if (!PilotRequestTracker.SatisfyOpenRequest(aircraft, PilotPendingRequestKind.Release, elapsedSeconds))
        {
            return;
        }

        world.AcknowledgeControllerResponse(aircraft.Callsign);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("The release answered the release request of {Callsign}; the frequency gate is cleared", aircraft.Callsign);
        }
    }

    /// <summary>
    /// Assemble the per-airport rundown of held departures, unioning held runway/airborne spawns from
    /// the delayed queue with held ground departures in the world, ordered so the first entry at each
    /// airport is the next-pending release.
    /// </summary>
    public static List<HeldDeparture> BuildRundown(SimScenarioState scenario, SimulationWorld world)
    {
        var result = new List<HeldDeparture>();

        foreach (DelayedSpawn entry in scenario.DelayedQueue)
        {
            if (!entry.HeldForRelease || !IsAirportArmed(scenario, DepartureAirportOf(entry.Aircraft.State)))
            {
                continue;
            }

            AircraftState state = entry.Aircraft.State;
            result.Add(
                new HeldDeparture(
                    state.Callsign,
                    DepartureAirportOf(state),
                    state.AircraftType,
                    state.FlightPlan.Destination,
                    IsGroundDeparture: false,
                    PendingSinceSeconds: entry.SpawnAtSeconds,
                    Status: "Holding (runway)"
                )
            );
        }

        foreach (AircraftState ac in world.GetSnapshot())
        {
            if (!ac.Ground.HeldForRelease)
            {
                continue;
            }

            result.Add(
                new HeldDeparture(
                    ac.Callsign,
                    DepartureAirportOf(ac),
                    ac.AircraftType,
                    ac.FlightPlan.Destination,
                    IsGroundDeparture: true,
                    PendingSinceSeconds: ac.SpawnedAtSeconds,
                    Status: GroundHoldStatus(ac)
                )
            );
        }

        result.Sort(
            (a, b) =>
            {
                int byAirport = string.CompareOrdinal(a.Airport, b.Airport);
                return byAirport != 0 ? byAirport : a.PendingSinceSeconds.CompareTo(b.PendingSinceSeconds);
            }
        );
        return result;
    }

    private static HeldDeparture? FindHeldByCallsign(SimScenarioState scenario, SimulationWorld world, string callsign) =>
        BuildRundown(scenario, world).FirstOrDefault(h => h.Callsign.Equals(callsign, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for an IFR departure that is on the ground and has not yet entered the takeoff sequence.</summary>
    private static bool IsHoldableGroundDeparture(AircraftState ac)
    {
        if (ac.FlightPlan.IsVfr || !ac.IsOnGround)
        {
            return false;
        }

        return ac.Phases?.CurrentPhase
            is AtParkingPhase
                or PushbackPhase
                or TugReleasePhase
                or HoldingAfterPushbackPhase
                or TaxiingPhase
                or HoldingShortPhase;
    }

    private static string GroundHoldStatus(AircraftState ac) =>
        ac.Phases?.CurrentPhase switch
        {
            HoldingShortPhase => "Holding short (held)",
            TaxiingPhase => "Taxiing (held)",
            AtParkingPhase => "At gate (held)",
            PushbackPhase or TugReleasePhase => "Pushing back (held)",
            HoldingAfterPushbackPhase => "Pushed back (held)",
            LinedUpAndWaitingPhase => "Lined up (held)",
            _ => "Held",
        };

    /// <summary>
    /// True when this delayed spawn must be held off the scope — a held runway/airborne departure
    /// whose airport is currently armed. Consulted by <c>ProcessDelayedSpawns</c>.
    /// </summary>
    public static bool IsSpawnHeld(SimScenarioState scenario, DelayedSpawn entry) =>
        entry.HeldForRelease && IsAirportArmed(scenario, DepartureAirportOf(entry.Aircraft.State));

    /// <summary>
    /// The held runway departures that spawn this tick held at their runway's bar (or lined up, held, where the runway has no
    /// bar): per armed field, the earliest due held runway candidate, by scheduled time then queue order, while no held
    /// departure of that field waits at a bar or on the runway and the place it would take is clear. Airborne-start candidates
    /// are never chosen: they stay gated until released. Derived from the queue and the world only, so a replay chooses alike.
    /// </summary>
    public static HashSet<DelayedSpawn> SelectHeldRunwaySpawns(SimScenarioState scenario, SimulationWorld world)
    {
        var chosen = new HashSet<DelayedSpawn>();
        if (scenario.HeldDepartureAirports.Count == 0)
        {
            return chosen;
        }

        var decided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DelayedSpawn entry in HeldCandidatesInOrder(scenario, airport: null))
        {
            AircraftState state = entry.Aircraft.State;
            if ((entry.SpawnAtSeconds > scenario.ElapsedSeconds) || (!IsSpawnHeld(scenario, entry)) || (!IsRunwayStart(state)))
            {
                continue;
            }

            if ((decided.Add(StripIcaoK(DepartureAirportOf(state)))) && (IsSpawnPlaceFree(world, state)))
            {
                chosen.Add(entry);
            }
        }

        return chosen;
    }

    /// <summary>The queued hold candidates, at <paramref name="airport"/> when given, by scheduled time then queue order.</summary>
    private static List<DelayedSpawn> HeldCandidatesInOrder(SimScenarioState scenario, string? airport) =>
        [
            .. scenario
                .DelayedQueue.Select((entry, index) => (entry, index))
                .Where(p => (p.entry.HeldForRelease) && ((airport is null) || AirportMatches(DepartureAirportOf(p.entry.Aircraft.State), airport)))
                .OrderBy(p => p.entry.SpawnAtSeconds)
                .ThenBy(p => p.index)
                .Select(p => p.entry),
        ];

    private static bool IsRunwayStart(AircraftState state) => state.Phases?.CurrentPhase is LinedUpAndWaitingPhase;

    /// <summary>
    /// True when <paramref name="state"/> may spawn held now: no held departure of its field waits at a bar or on the runway, and
    /// no aircraft holds short at the bar it would take (or, with no bar, is lined up on its runway).
    /// </summary>
    private static bool IsSpawnPlaceFree(SimulationWorld world, AircraftState state)
    {
        string airport = DepartureAirportOf(state);
        GroundNode? bar = HeldRunwaySpawnBuilder.FindDepartureBar(state);
        foreach (AircraftState ac in world.GetSnapshot())
        {
            if ((ac.IsOnGround) && ((IsHeldAtTheRunway(ac, airport)) || (OccupiesSpawnPlace(ac, state, bar))))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The held departure of <paramref name="airport"/> waiting at a bar or lined up, if any; the earliest spawned first.</summary>
    private static AircraftState? HeldAtTheRunway(SimulationWorld world, string airport) =>
        world.GetSnapshot().Where(ac => (ac.IsOnGround) && IsHeldAtTheRunway(ac, airport)).OrderBy(ac => ac.SpawnedAtSeconds).FirstOrDefault();

    private static bool IsHeldAtTheRunway(AircraftState ac, string airport) =>
        (ac.Ground.HeldForRelease)
        && (ac.Phases?.CurrentPhase is HoldingShortPhase or LinedUpAndWaitingPhase)
        && AirportMatches(DepartureAirportOf(ac), airport);

    private static bool OccupiesSpawnPlace(AircraftState ac, AircraftState spawning, GroundNode? bar) =>
        ac.Phases?.CurrentPhase switch
        {
            HoldingShortPhase holding => (bar is not null)
                && (holding.HoldShort.NodeId == bar.Id)
                && AirportMatches(DepartureAirportOf(ac), DepartureAirportOf(spawning)),
            LinedUpAndWaitingPhase => (bar is null)
                && (ac.Phases.AssignedRunway?.Designator is { } runway)
                && (runway == spawning.Phases?.AssignedRunway?.Designator)
                && AirportMatches(DepartureAirportOf(ac), DepartureAirportOf(spawning)),
            _ => false,
        };

    /// <summary>
    /// If <paramref name="ac"/> is a holdable IFR ground departure whose airport is armed, mark it
    /// held for release so it holds short of the runway until released. Called when a departure spawns
    /// under a (possibly already-armed) airport.
    /// </summary>
    public static void MarkHeldOnSpawnIfArmed(SimScenarioState scenario, AircraftState ac)
    {
        if (!ac.Ground.HeldForRelease && IsHoldableGroundDeparture(ac) && IsAirportArmed(scenario, DepartureAirportOf(ac)))
        {
            ac.Ground.HeldForRelease = true;
        }
    }

    /// <summary>True when <paramref name="airport"/> matches any airport currently armed for hold-for-release.</summary>
    public static bool IsAirportArmed(SimScenarioState scenario, string airport)
    {
        foreach (string armed in scenario.HeldDepartureAirports)
        {
            if (AirportMatches(armed, airport))
            {
                return true;
            }
        }

        return false;
    }

    private static string DepartureAirportOf(AircraftState ac) => (ac.FlightPlan.Departure ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Match a departure airport against an armed-airport entry, tolerating the FAA/ICAO "K" prefix (KSJC == SJC).</summary>
    public static bool AirportMatches(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return StripIcaoK(a).Equals(StripIcaoK(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripIcaoK(string id) => id is { Length: 4 } && (id[0] == 'K' || id[0] == 'k') ? id[1..] : id;
}
