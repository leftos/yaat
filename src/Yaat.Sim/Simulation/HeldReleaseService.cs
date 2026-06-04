using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

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
/// Pure state mutator for hold-for-release: arm/disarm an airport, release a departure, and build the
/// per-airport rundown. All hold-for-release state lives in <see cref="SimScenarioState"/> and on
/// <see cref="AircraftGroundOps"/>; this service is the only writer. The server owns broadcasting the
/// resulting rundown.
/// </summary>
public static class HeldReleaseService
{
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
        var normalized = airport.Trim().ToUpperInvariant();
        if (normalized.Length == 0)
        {
            return new HeldReleaseResult(false, "HFR requires an airport");
        }

        scenario.HeldDepartureAirports.Add(normalized);

        var swept = 0;
        foreach (var ac in world.GetSnapshot())
        {
            if (!ac.Ground.HeldForRelease && IsHoldableGroundDeparture(ac) && AirportMatches(DepartureAirportOf(ac), normalized))
            {
                ac.Ground.HeldForRelease = true;
                ac.Ground.ReleasedForDeparture = false;
                swept++;
            }
        }

        var suffix = swept > 0 ? $" ({swept} departure{(swept == 1 ? "" : "s")} now holding)" : "";
        return new HeldReleaseResult(true, $"Hold for release armed at {normalized}{suffix}");
    }

    /// <summary>Disarm an airport; auto-releases anything still held there (ground departures and queued spawns).</summary>
    public static HeldReleaseResult Disarm(SimScenarioState scenario, SimulationWorld world, string airport)
    {
        var normalized = airport.Trim().ToUpperInvariant();
        if (!scenario.HeldDepartureAirports.Remove(normalized))
        {
            return new HeldReleaseResult(false, $"{normalized} is not armed for hold for release");
        }

        // Released ground departures: clear the flag and authorize roll.
        var released = 0;
        foreach (var ac in world.GetSnapshot())
        {
            if (ac.Ground.HeldForRelease && AirportMatches(DepartureAirportOf(ac), normalized))
            {
                ac.Ground.HeldForRelease = false;
                ac.Ground.ReleasedForDeparture = true;
                ac.Ground.ReleasedAtSeconds = scenario.ElapsedSeconds;
                released++;
            }
        }

        // Queued held spawns at this airport become spawn-eligible immediately (the armed-set check in
        // ProcessDelayedSpawns now returns false). Drop any pending scheduled releases for the field.
        scenario.ReleaseQueue.RemoveAll(r => AirportMatches(r.Airport, normalized));

        var suffix = released > 0 ? $" ({released} released)" : "";
        return new HeldReleaseResult(true, $"Hold for release disarmed at {normalized}{suffix}");
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
        var normalized = target.Trim().ToUpperInvariant();

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
        for (var i = 0; i < atField.Count; i++)
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

        var minutes = interval / 60.0;
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
            var ac = world.FindAircraft(held.Callsign);
            if (ac is null || !ac.Ground.HeldForRelease)
            {
                return new HeldReleaseResult(false, $"{held.Callsign} is no longer held");
            }

            ac.Ground.HeldForRelease = false;
            ac.Ground.ReleasedForDeparture = true;
            ac.Ground.ReleasedAtSeconds = scenario.ElapsedSeconds;
            return new HeldReleaseResult(true, $"{held.Callsign} released");
        }

        var entry = scenario.DelayedQueue.FirstOrDefault(d =>
            d.HeldForRelease && d.Aircraft.State.Callsign.Equals(held.Callsign, StringComparison.OrdinalIgnoreCase)
        );
        if (entry is null)
        {
            return new HeldReleaseResult(false, $"{held.Callsign} is no longer held");
        }

        entry.HeldForRelease = false;
        var jitter =
            bakedJitter
            ?? rng?.Next((int)MinSpawnReleaseDelaySeconds, (int)MaxSpawnReleaseDelaySeconds + 1)
            ?? throw new InvalidOperationException("ReleaseOneCore requires either a baked jitter or an RNG to sample.");
        entry.SpawnAtSeconds = (int)scenario.ElapsedSeconds + jitter;
        return new HeldReleaseResult(true, $"{held.Callsign} released") { SpawnJitterSeconds = jitter };
    }

    /// <summary>
    /// Assemble the per-airport rundown of held departures, unioning held runway/airborne spawns from
    /// the delayed queue with held ground departures in the world, ordered so the first entry at each
    /// airport is the next-pending release.
    /// </summary>
    public static List<HeldDeparture> BuildRundown(SimScenarioState scenario, SimulationWorld world)
    {
        var result = new List<HeldDeparture>();

        foreach (var entry in scenario.DelayedQueue)
        {
            if (!entry.HeldForRelease || !IsAirportArmed(scenario, DepartureAirportOf(entry.Aircraft.State)))
            {
                continue;
            }

            var state = entry.Aircraft.State;
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

        foreach (var ac in world.GetSnapshot())
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
                var byAirport = string.CompareOrdinal(a.Airport, b.Airport);
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

        return ac.Phases?.CurrentPhase is AtParkingPhase or PushbackPhase or HoldingAfterPushbackPhase or TaxiingPhase or HoldingShortPhase;
    }

    private static string GroundHoldStatus(AircraftState ac) =>
        ac.Phases?.CurrentPhase switch
        {
            HoldingShortPhase => "Holding short",
            TaxiingPhase => "Taxiing (held)",
            AtParkingPhase or PushbackPhase or HoldingAfterPushbackPhase => "At gate (held)",
            _ => "Held",
        };

    /// <summary>
    /// True when this delayed spawn must be held off the scope — a held runway/airborne departure
    /// whose airport is currently armed. Consulted by <c>ProcessDelayedSpawns</c>.
    /// </summary>
    public static bool IsSpawnHeld(SimScenarioState scenario, DelayedSpawn entry) =>
        entry.HeldForRelease && IsAirportArmed(scenario, DepartureAirportOf(entry.Aircraft.State));

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
        foreach (var armed in scenario.HeldDepartureAirports)
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
    private static bool AirportMatches(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return StripIcaoK(a).Equals(StripIcaoK(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripIcaoK(string id) => id is { Length: 4 } && (id[0] == 'K' || id[0] == 'k') ? id[1..] : id;
}
