using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Pilot;
using Yaat.Sim.Training;

namespace Yaat.Sim;

public sealed class SimulationWorld
{
    private static readonly ILogger Log = SimLog.CreateLogger("SimulationWorld");

    private readonly object _lock = new();
    private readonly List<AircraftState> _aircraft = [];
    private readonly List<CompletedAircraftRecord> _completedAircraft = [];

    /// <summary>
    /// FIFO cap on <see cref="_completedAircraft"/>. Long training sessions (hundreds of
    /// arrivals/departures) would otherwise grow this list unbounded — it rides every
    /// <c>GetSessionReport</c> response (server-side 2 s and 5 s poll cadences) and the
    /// Aircraft tab DataGrid would grow monotonically. 500 entries covers a busy
    /// multi-hour session while keeping the per-poll DTO size sane.
    /// </summary>
    internal const int CompletedAircraftCapacity = 500;

    public SerializableRandom Rng { get; set; } = new SerializableRandom(0);

    /// <summary>
    /// Dedicated RNG for sampling per-command pilot-reaction delays (the command-run delay). Kept
    /// separate from <see cref="Rng"/> so reaction sampling never perturbs the shared RNG stream that
    /// drives replay-critical emergent events (spontaneous go-arounds, generator spawn timing).
    /// Live-only: replays reproduce the exact delay baked into the recorded command rather than
    /// re-sampling, so this RNG is never consumed during replay and is intentionally not snapshotted.
    /// </summary>
    public SerializableRandom ReactionDelayRng { get; set; } = new SerializableRandom(0);

    public WeatherProfile? Weather { get; set; }
    public AirportGroundLayout? GroundLayout { get; set; }
    public FrequencyState ActiveFrequency { get; } = new();

    /// <summary>
    /// Student TCP. Handoff accepts to this position do not trigger ONHO conditions.
    /// Set from the scenario's <c>studentPositionId</c> → resolved <see cref="Tcp"/>.
    /// </summary>
    public Tcp? StudentTcp { get; set; }

    /// <summary>
    /// Cached scenario flag — set by <c>SimulationEngine</c> before each tick. Read by
    /// <see cref="FlightPhysics.Update(AircraftState, double, Func{string, AircraftState?}?, WeatherProfile?, bool, bool)"/>
    /// to route sim-initiated pilot transmissions.
    /// </summary>
    public bool SoloTrainingMode { get; set; }

    /// <summary>
    /// Cached scenario flag — set by <c>SimulationEngine</c> before each tick. When true (and
    /// <see cref="SoloTrainingMode"/> is false), sim-initiated pilot transmissions are routed
    /// to <c>PendingPilotSpeech</c> instead of <c>PendingWarnings</c>.
    /// </summary>
    public bool RpoShowPilotSpeech { get; set; }

    public void AddAircraft(AircraftState aircraft)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(aircraft.Cid))
            {
                aircraft.Cid = GenerateUniqueCid();
            }

            // Replacement-safe: drop any pre-existing entry with the same callsign before
            // appending. A user-typed VP/DA creates an unsupported-ghost AircraftState; if a
            // scenario delayed-spawn / generator / ADD-spawn later arrives with the same
            // callsign, the appended duplicate makes the 1Hz broadcast pump two DTOs per tick
            // and the client AircraftList flickers between them. Replace policy: spawning
            // aircraft wins, ghost (and any user-typed FP/scratchpads/track ownership) is
            // discarded. Warn so unintentional collisions surface in logs.
            int removed = _aircraft.RemoveAll(a => string.Equals(a.Callsign, aircraft.Callsign, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Log.LogWarning(
                    "AddAircraft({Callsign}): replaced {Count} pre-existing entry/entries with the same callsign (likely a user-typed FP ghost colliding with a scenario or runtime spawn).",
                    aircraft.Callsign,
                    removed
                );
            }

            // Same-callsign respawn after a removal: drop any stale completed record so the
            // M12.4 Aircraft tab shows only the live aircraft. The aggregator dedupe
            // (live wins) handles this while the new aircraft is in-world, but when this
            // run later completes and gets removed, both records would resurface without
            // this purge. Mirror policy: live spawn wins over a stale completion record.
            _completedAircraft.RemoveAll(r => string.Equals(r.Callsign, aircraft.Callsign, StringComparison.OrdinalIgnoreCase));

            _aircraft.Add(aircraft);
        }
    }

    public void RemoveAircraft(string callsign)
    {
        lock (_lock)
        {
            for (int i = _aircraft.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_aircraft[i].Callsign, callsign, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ac = _aircraft[i];
                // Canonical "is this completed?" check matches the rest of the codebase
                // (StampHandoffCompletion, TickTouchdown). CompletedAtSeconds is set
                // alongside CompletionReason today; keying off the reason guards against
                // a future write path that sets one without the other.
                if (ac.CompletionReason != CompletionReason.Active)
                {
                    _completedAircraft.Add(
                        new CompletedAircraftRecord(
                            ac.Callsign,
                            ac.AircraftType,
                            ac.Cid,
                            string.IsNullOrEmpty(ac.FlightPlan.Departure) ? null : ac.FlightPlan.Departure,
                            string.IsNullOrEmpty(ac.FlightPlan.Destination) ? null : ac.FlightPlan.Destination,
                            ac.SpawnedAtSeconds,
                            ac.CompletedAtSeconds ?? 0,
                            ac.CompletionReason,
                            ac.CompletionDetail
                        )
                    );
                    if (_completedAircraft.Count > CompletedAircraftCapacity)
                    {
                        _completedAircraft.RemoveRange(0, _completedAircraft.Count - CompletedAircraftCapacity);
                    }
                }

                _aircraft.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Snapshot of aircraft that have left the active set with a recorded completion stamp.
    /// Used by <c>SoloTrainingEvaluator</c> to keep the M12.4 debrief tab populated after
    /// landings, handoffs, or deletes drop the aircraft from <c>SimulationWorld</c>.
    /// </summary>
    public IReadOnlyList<CompletedAircraftRecord> GetCompletedAircraft()
    {
        lock (_lock)
        {
            return [.. _completedAircraft];
        }
    }

    /// <summary>
    /// Re-evaluates implicit RunwayCrossing hold-shorts on every aircraft's active
    /// taxi route against the supplied AutoCross setting. Called by the server when
    /// the controller toggles AutoCrossRunway mid-session, and by the engine's replay
    /// applier when a recorded toggle is replayed. Aircraft currently sitting in
    /// <see cref="Yaat.Sim.Phases.Ground.HoldingShortPhase"/> are intentionally left
    /// alone — only future crossings on each route are updated.
    /// </summary>
    public void ApplyAutoCrossToActiveTaxiRoutes(bool autoCross)
    {
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                var route = ac.Ground.AssignedTaxiRoute;
                if (route is null)
                {
                    continue;
                }

                Data.Airport.TaxiRouteAutoCross.Apply(route, autoCross);
            }
        }
    }

    public List<AircraftState> GetSnapshot()
    {
        lock (_lock)
        {
            return [.. _aircraft];
        }
    }

    /// <summary>
    /// Look up an aircraft by callsign (case-insensitive). Used by phases that
    /// need to resolve a follow target — e.g. <see cref="Phases.Pattern.VfrFollowPhase"/>
    /// and <see cref="Phases.AirborneFollowHelper"/>. The lock is reentrant, so
    /// this is safe to call from inside <see cref="Tick(double, Action{AircraftState, double}?)"/>.
    /// </summary>
    public AircraftState? FindAircraft(string callsign)
    {
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (string.Equals(ac.Callsign, callsign, StringComparison.OrdinalIgnoreCase))
                {
                    return ac;
                }
            }
            return null;
        }
    }

    public void Tick(double deltaSeconds)
    {
        Tick(deltaSeconds, preTick: null, timingCallback: null);
    }

    public void Tick(double deltaSeconds, Action<AircraftState, double>? preTick)
    {
        Tick(deltaSeconds, preTick, timingCallback: null);
    }

    /// <summary>
    /// Tick with optional timing instrumentation. <paramref name="timingCallback"/>
    /// is invoked with (bucketName, elapsedMs) for each timed section. Used by
    /// <see cref="Yaat.Sim.Simulation.SimulationEngine"/> test diagnostics to
    /// break down per-tick cost. Overhead is ~1µs per bucket when the callback
    /// is null-checked (the Stopwatch allocations are skipped in that path).
    /// </summary>
    public void Tick(double deltaSeconds, Action<AircraftState, double>? preTick, Action<string, double>? timingCallback)
    {
        lock (_lock)
        {
            AircraftState? Lookup(string callsign)
            {
                foreach (var a in _aircraft)
                {
                    if (string.Equals(a.Callsign, callsign, StringComparison.OrdinalIgnoreCase))
                    {
                        return a;
                    }
                }

                return null;
            }

            Stopwatch? sw = timingCallback is not null ? new Stopwatch() : null;

            sw?.Restart();
            GroundConflictDetector.ApplySpeedLimits(_aircraft, GroundLayout, deltaSeconds);
            if (sw is not null)
            {
                sw.Stop();
                timingCallback!("World.GroundConflict", sw.Elapsed.TotalMilliseconds);
            }

            var weather = Weather;
            var studentTcp = StudentTcp;
            var soloMode = SoloTrainingMode;
            var rpoShowPilotSpeech = RpoShowPilotSpeech;
            foreach (var ac in _aircraft)
            {
                // Student TCP accepts don't trigger ONHO conditions
                if (ac.Track.HandoffAccepted && studentTcp is not null && ac.Track.Owner?.IsTcp(studentTcp) == true)
                {
                    ac.Track.HandoffAccepted = false;
                }

                preTick?.Invoke(ac, deltaSeconds);

                if (timingCallback is not null)
                {
                    var acSw = Stopwatch.StartNew();
                    bool onGround = ac.IsOnGround;
                    FlightPhysics.Update(ac, deltaSeconds, Lookup, weather, soloMode, rpoShowPilotSpeech);
                    acSw.Stop();
                    timingCallback(onGround ? "World.Physics.Ground" : "World.Physics.Air", acSw.Elapsed.TotalMilliseconds);
                }
                else
                {
                    FlightPhysics.Update(ac, deltaSeconds, Lookup, weather, soloMode, rpoShowPilotSpeech);
                }
            }
        }
    }

    public List<(string Callsign, string Warning)> DrainAllWarnings()
    {
        var result = new List<(string, string)>();
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.PendingWarnings.Count > 0)
                {
                    foreach (var w in ac.PendingWarnings)
                    {
                        result.Add((ac.Callsign, w));
                    }

                    ac.PendingWarnings.Clear();
                }
            }
        }

        return result;
    }

    public List<(string Callsign, string Notification)> DrainAllNotifications()
    {
        var result = new List<(string, string)>();
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.PendingNotifications.Count > 0)
                {
                    foreach (var n in ac.PendingNotifications)
                    {
                        result.Add((ac.Callsign, n));
                    }

                    ac.PendingNotifications.Clear();
                }
            }
        }

        return result;
    }

    public List<(string Callsign, string PilotSpeech)> DrainAllPilotSpeech()
    {
        var result = new List<(string, string)>();
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.PendingPilotSpeech.Count > 0)
                {
                    foreach (var s in ac.PendingPilotSpeech)
                    {
                        result.Add((ac.Callsign, s));
                    }

                    ac.PendingPilotSpeech.Clear();
                }
            }
        }

        return result;
    }

    public List<(string Callsign, string Readback)> DrainAllPilotReadbacks()
    {
        var result = new List<(string, string)>();
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.PendingPilotReadbacks.Count > 0)
                {
                    foreach (var s in ac.PendingPilotReadbacks)
                    {
                        result.Add((ac.Callsign, s));
                    }

                    ac.PendingPilotReadbacks.Clear();
                }
            }
        }

        return result;
    }

    public void ExpectPilotReadback(string callsign, double elapsedSeconds)
    {
        ActiveFrequency.ExpectReadback(callsign, elapsedSeconds);
    }

    /// <summary>
    /// Clears the awaiting-controller-response gate when the controller dispatches a
    /// command to <paramref name="callsign"/> in solo-training mode. If the command also
    /// produces a readback the readback gate independently takes over for ordering;
    /// either way other pilots' proactive transmissions are no longer held by the
    /// controller-response gate for this callsign.
    /// </summary>
    public void AcknowledgeControllerResponse(string callsign)
    {
        ActiveFrequency.AcknowledgeControllerResponse(callsign);
    }

    public List<PilotTransmission> DrainReadyPilotTransmissions(double elapsedSeconds)
    {
        var result = new List<PilotTransmission>();
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.PendingPilotTransmissions.Count > 0)
                {
                    foreach (var transmission in ac.PendingPilotTransmissions)
                    {
                        ActiveFrequency.Enqueue(transmission);
                    }

                    ac.PendingPilotTransmissions.Clear();
                }
            }

            while (ActiveFrequency.TryDequeueReady(elapsedSeconds) is { } transmission)
            {
                result.Add(transmission);
            }
        }

        return result;
    }

    public void DiscardAllPilotTransmissions()
    {
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                ac.PendingPilotTransmissions.Clear();
            }

            ActiveFrequency.Clear();
        }
    }

    public List<ApproachScore> DrainAllApproachScores()
    {
        var result = new List<ApproachScore>();
        lock (_lock)
        {
            foreach (var ac in _aircraft)
            {
                if (ac.PendingApproachScores.Count > 0)
                {
                    result.AddRange(ac.PendingApproachScores);
                    ac.PendingApproachScores.Clear();
                }
            }
        }

        return result;
    }

    public int Clear()
    {
        lock (_lock)
        {
            int count = _aircraft.Count;
            _aircraft.Clear();
            _completedAircraft.Clear();
            GroundLayout = null;
            return count;
        }
    }

    public uint GenerateBeaconCode() => GenerateBeaconCode(Rng);

    public static uint GenerateBeaconCode(Random rng)
    {
        uint code = 0;
        for (int i = 0; i < 4; i++)
        {
            code = (code * 10) + (uint)rng.Next(0, 8);
        }

        return code;
    }

    /// <summary>
    /// Must be called under _lock.
    /// </summary>
    private string GenerateUniqueCid()
    {
        var usedCids = new HashSet<string>();
        foreach (var ac in _aircraft)
        {
            if (!string.IsNullOrEmpty(ac.Cid))
            {
                usedCids.Add(ac.Cid);
            }
        }

        var rng = Rng;
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            var cid = rng.Next(100, 1000).ToString();
            if (!usedCids.Contains(cid))
            {
                return cid;
            }
        }

        for (int i = 100; i < 1000; i++)
        {
            var cid = i.ToString();
            if (!usedCids.Contains(cid))
            {
                return cid;
            }
        }

        return "000";
    }
}
