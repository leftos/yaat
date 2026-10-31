// =============================================================================
// GroundConflictDetector
//
// Per-tick airport ground conflict detector. Classifies each pair of ground
// aircraft into exactly ONE pair kind, runs one handler, writes SpeedLimit on
// the affected aircraft. Phases and physics honor that limit.
//
// Pair classification:
//
//   • Distant           — beyond search range; skip.
//   • Stationary        — both aircraft parked / holding; skip.
//   • Pushback          — at least one pushing back; pushback-buffer logic only.
//   • SameEdgeTrailing  — both on same edge, same direction. Trailer slows to
//     leader's speed (or stops if too close).
//   • SameEdgeHeadOn    — same edge, opposite direction. Pick a deterministic
//     holder (aircraft with more route remaining; tie-break by callsign). One
//     aircraft proceeds, one holds — avoids the mutual-stop deadlock that the
//     earlier "both stop" rule produced once two routes resolve to a single
//     single-lane segment.
//   • Converging        — routes share an upcoming node from different edges.
//     Yielder is whichever aircraft is farther from the shared node. Closing
//     proximity still runs as the physical-overlap safety net (the
//     wingspan-lateral-clearance bypass handles the merge geometry); head-on
//     is suppressed for this pair this tick.
//   • Crossing          — close in space, no shared route node, not same-edge.
//     Resolved to one-holds-one-goes (ResolveCrossing): an aircraft on the
//     runway surface has priority; a yielder keeps its heading-based closing
//     pin even when stopped (no self-pin crawl); if both would stop,
//     ChooseMutualStopHolder picks the holder: a live-traffic shadow is never
//     held; the aircraft with the other clearly more dead ahead holds;
//     otherwise a FOLLOWG follower facing an aircraft that is not following
//     keeps its place and the other holds; else a deterministic callsign
//     tie-break. Head-on fallback (both stop) applies only to near-anti-parallel
//     approaches (>= HeadOnMinHeadingDiffDeg); oblique crossings use the holder
//     arbitration.
//
// Hold classification: a routed aircraft with Ground.Hold set (HOLDPOSITION or
// GIVEWAY) classifies as Stationary. It won't move until the resume condition
// fires (FlightPhysics.UpdateGiveWayResume for GIVEWAY, operator RES for either),
// so other aircraft can pass laterally with wingspan clearance. The diagnostic
// log distinguishes the kind of hold so DebugSink consumers can tell whether the
// stop is intent-bearing ("Yielding to SWA123") or unconditional ("HoldPosition").
//
// FOLLOWG followers: a follower classifies on the route it drives (FollowingPhase.DrivenRouteOf, its follow or clearing route):
// Taxiing while it has one, else through the same rest gates as any aircraft with no route — Stationary at rest, an Untracked
// mover on its heading when rolling. A follower is never limited against an aircraft in its lead chain (its lead by
// FollowingPhase.TargetCallsign, that lead's lead, and so on), since the follows keep their own gaps along the chain; a lead in
// that chain is exempt from its follower only while it is not closing on it. A lead closes on its follower when its push track
// points within 90° of it, or when it re-enters its own TaxiEdgeTrail with the follower on a trail edge: the edge it is on is a
// trail edge older than the newest (retracing; the one just before the newest only when the two share no node); it is coming
// back over the fillet arc between the node it entered its newest edge from and an older trail edge; or, on its newest edge
// itself and not rounding an arc, its track points within 90° of the node it entered that edge from (turned about). A closing
// lead is limited as in any other pair; when the pair's resolution limits only the follower, which cannot yield, the lead takes
// that limit instead, fitted to its own brakes. A sibling follower of the same lead is an ordinary pair. A follower stopped at a bar (a
// HoldingShortPhase with its FollowingPhase queued behind) is not following here — the chain walk reads only the current
// phase — whereas RunwayDepartureQueue still ranks it as following its lead.
//
// Public API:
//   - ApplySpeedLimits(List<AircraftState>, AirportGroundLayout?, double, Action<string>?)
//   - IsClearOf(AircraftState, AircraftState, AirportGroundLayout?)
//   - DebugSink, WingspanLateralCheckEnabled, WingspanLateralCheckRequireStationary
// =============================================================================

using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.LiveTraffic;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim;

public static class GroundConflictDetector
{
    private static readonly ILogger Log = SimLog.CreateLogger("GroundConflictDetector");

    private const double DefaultTrailDistanceFt = 200.0;

    /// <summary>Floor on the stop distance <see cref="GetSeparation"/> returns, for pairs too short to earn a larger one.</summary>
    public const double DefaultStopDistanceFt = 100.0;

    /// <summary>Gap left between the two fuselage ends at a stop, on top of the pair's half-lengths (<see cref="GetSeparation"/>).</summary>
    public const double StopBufferFt = 25.0;

    /// <summary>
    /// How often <see cref="ApplySpeedLimits"/> runs, seconds: once per physics sub-tick
    /// (<see cref="SimulationEngine.PhysicsSubTickRate"/> of them per simulated second), which is the next chance the
    /// outline sweep has to re-measure a tug move's braking limit (<see cref="TugMoveStopMarginFt"/>,
    /// <see cref="TugMoveBrakingLimitKts"/>) — so one interval of travel is unbraked in both.
    /// </summary>
    private const double DetectorIntervalSeconds = 1.0 / SimulationEngine.PhysicsSubTickRate;
    private const double OppositeStopDistanceFt = 300.0;
    private const double PushbackBufferFt = 200.0;
    internal const double SlowTaxiSpeedKts = 5.0;

    /// <summary>How far past the current separation <see cref="RouteLateralClearanceFt"/> looks along the route.</summary>
    private const double RouteClearanceBoundFactor = 1.5;

    /// <summary>Chords per fillet arc when measuring route clearance; 8 keeps the sampling error well under a foot.</summary>
    private const int ArcClearanceSamples = 8;

    /// <summary>Step (ft) along the held aircraft's route when searching for its give-way stop (<see cref="GiveWayStop"/>).</summary>
    private const double GiveWayStopStepFt = 5.0;

    /// <summary>How finely (ft) the give-way stop is resolved inside the step that reaches the clearance.</summary>
    private const double GiveWayStopResolutionFt = 0.1;
    internal const double HeldStationarySpeedKts = 3.0;
    private const double FtPerNm = 6076.12;
    private const double ConvergenceLookaheadFt = 1500.0;
    private const double ConvergenceSlowdownFt = 400.0;

    // Convergence ETA gate: skip the slowdown when the nearer aircraft will clear the shared node at
    // least this many seconds before the farther aircraft reaches it — the crossing is already clear,
    // so braking the farther aircraft achieves nothing.
    private const double ConvergenceClearanceBufferSec = 10.0;

    // The nearer aircraft must be moving faster than this for the ETA gate to trust that it clears
    // first; below it, keep the slowdown (a near-stopped "winner" might not clear in time).
    private const double ConvergenceMinWinnerSpeedKts = 3.0;

    // Unrestricted taxi speed assumed for the farther aircraft's arrival ETA, so an aircraft already
    // capped by this rule does not feed its reduced speed back in and oscillate.
    private const double ConvergenceNominalTaxiSpeedKts = 12.0;
    internal const double SearchRangeNm = 0.3;

    // A head-on requires near-anti-parallel headings. Oblique crossings (e.g. an
    // aircraft exiting a runway toward its hold-short passing one taxiing to the
    // apron, ~130° apart) are NOT head-ons — stopping both there is the symmetric
    // mutual-stop that real ground control never does. They resolve via the
    // closing/arbitration rules (one holds, one goes) instead.
    private const double HeadOnMinHeadingDiffDeg = 150.0;

    // How far two tracks may differ from parallel (0°) or anti-parallel (180°) and still count as
    // neighbouring lanes for HasParallelTrackLateralRoom. Two aircraft on adjacent taxiways pass each
    // other; they are not closing on one another, whatever the straight-line distance says. Measured at
    // SFO, taxiways A and B: the centrelines are ~160 ft apart and opposite-direction passes bottom out
    // at 238 ft, inside both the two-B738 trail ring (254 ft) and the 300 ft head-on ring — so without
    // this bypass a B738 crawled at 5 kt for 65 s and stopped for 20 s while three aircraft passed on
    // the neighbouring lane. The tolerance covers a lane's own curvature and the wander of a nose
    // heading within it, while a genuine crossing (more than 20° off) keeps the distance rule. The 20°
    // has no FAA basis — a judgement call. Two tracks converging inside the tolerance still keep the bypass while
    // their lateral offset exceeds the requirement, but not on the current offset alone: the offsets are also
    // projected ParallelTrackLookAheadCapSeconds ahead, so a pair that would close through the requirement inside its
    // own stopping time loses the bypass while it still has the room to brake. The projection follows the aircraft's
    // current taxi-route segment where it has one and that segment runs within this tolerance of its travel direction,
    // so a nose wandered a few degrees inside its lane does not spend the lane's margin on the wander; the travel
    // direction is the fallback (no route, a finished route, a pushback running against its own segment). The offsets
    // are signed, so a pair that crosses a track line anywhere inside the look-ahead loses the bypass whatever the two
    // ends of the projection measure. Exactly parallel or anti-parallel tracks keep their offset under it and pass as
    // before. A jet survives the instantaneous test alone (a B738 stops from 20 kt in ~68 ft against a 142 ft
    // requirement); the known edge it missed is two pistons on twin lanes (61 ft requirement vs ~95 ft to stop at
    // 2 kt/s).
    private const double ParallelTrackToleranceDeg = 20.0;

    /// <summary>How far ahead, seconds, the parallel-track bypass projects the two tracks before granting it: the
    /// longer of the pair's own stopping times, capped here so a slow-category aircraft's stopping time at speed
    /// cannot project the pair far down the taxiway. Public because the detector's tests build their geometry — a
    /// crossing that has to fall inside it — from the same figure.</summary>
    public const double ParallelTrackLookAheadCapSeconds = 12.0;

    // When two same-priority movers would each stop for the other, hold the "follower" (the one
    // with the other nearer dead-ahead — a small off-nose angle) and release the "lead" (the one
    // with the other more abeam), which increases separation as it proceeds. This is the auto
    // equivalent of FOLLOW/BEHIND sequencing (7110.65 3-7-2.a). Only trust it when the two
    // off-nose angles differ by at least this margin; below it the geometry is effectively
    // symmetric (a true perpendicular crossing / near head-on) with no follow relationship, so the
    // deterministic callsign tie-break stands. Purely a function of positions+headings+callsigns,
    // so it is deterministic and cannot oscillate.
    private const double FollowerLeadOffNoseMarginDeg = 30.0;

    public static Action<string>? DebugSink { get; set; }
    public static bool WingspanLateralCheckEnabled { get; set; } = true;
    public static bool WingspanLateralCheckRequireStationary { get; set; } = true;

    /// <summary>
    /// The tug run behind each towed mover's current move for the <see cref="ApplySpeedLimits"/> pass in flight
    /// (<see cref="TugRunContinuation"/>), keyed by mover. Thread-static, opened by the pass and dropped when it ends,
    /// so two threads running the detector never share one and nothing outlives the pass that built it.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<AircraftState, IReadOnlyList<TugMove>>? _tugRunContinuations;

    /// <summary>
    /// Each aircraft's <see cref="RunwayRank"/> for the <see cref="ApplySpeedLimits"/> pass in flight, computed by
    /// <see cref="BuildEntries"/> for every entry before any pair is resolved, from state a snapshot restores. Thread-static,
    /// opened by the pass and dropped when it ends, like <see cref="_tugRunContinuations"/>; outside a pass every aircraft takes
    /// its phase-only rank (<see cref="FloorRank"/>).
    /// </summary>
    [ThreadStatic]
    private static Dictionary<AircraftState, RunwayRank>? _runwayRanks;

    /// <summary>
    /// The aircraft the crossing stop curve limited in the pass before the <see cref="ApplySpeedLimits"/> pass in flight
    /// (<see cref="AircraftGroundOps.CrossingStopCurveCarry"/>, read and cleared by <see cref="ResetGroundState"/>), which keep
    /// the curve this pass (<see cref="HasCrossingStopCurve"/>). Thread-static, opened by the pass and dropped when it ends, like
    /// <see cref="_tugRunContinuations"/>; outside a pass none is carried.
    /// </summary>
    [ThreadStatic]
    private static HashSet<AircraftState>? _crossingStopCurveCarried;

    /// <summary>
    /// The closure travel (ft) each aircraft in <see cref="_crossingStopCurveCarried"/> carried into the pass in flight
    /// (<see cref="AircraftGroundOps.CrossingStopCurveBoundClosureTravelFt"/>, read by <see cref="ResetGroundState"/>): the
    /// largest travel of traffic toward it the crossing stop curve counted since its carry began, which
    /// <see cref="CrossingStopCurveReleases"/> reads as the pass started rather than as the pass's own limits raise it, whichever
    /// pair the pass resolves first. Thread-static, opened and dropped with <see cref="_crossingStopCurveCarried"/>; outside a
    /// pass none is known.
    /// </summary>
    [ThreadStatic]
    private static Dictionary<AircraftState, double>? _crossingStopCurveBoundTravel;

    /// <summary>
    /// The aircraft the pass before the <see cref="ApplySpeedLimits"/> pass in flight braked
    /// (<see cref="AircraftGroundOps.BrakedByDetectorLastPass"/>, read and cleared by <see cref="ResetGroundState"/>), so
    /// <see cref="ClosureOf"/> judges whether traffic is braking from its state at the start of the pass, whichever pair the
    /// pass resolves first. Thread-static, opened by the pass and dropped when it ends, like <see cref="_tugRunContinuations"/>;
    /// outside a pass none is known.
    /// </summary>
    [ThreadStatic]
    private static HashSet<AircraftState>? _brakedLastPass;

    /// <summary>
    /// The limit reason of the crossing stop curve (<see cref="CrossingStopCurveKts"/>) on a closing or trailing pair, which
    /// carries the curve on to the next pass.
    /// </summary>
    private const string CrossingStopCurveReason = "crossing stop curve";

    /// <summary>
    /// The limit reason of the crossing stop curve (<see cref="CrossingStopCurveKts"/>) on a yielder at a converging pair's
    /// shared node, which carries the curve on to the next pass.
    /// </summary>
    private const string ConvergenceCrossingStopCurveReason = "convergence crossing stop curve";

    /// <summary>
    /// Who gives way to whom across a runway (7110.65 3-1-3; AIM 4-3-21.a): a runway user first, an aircraft still vacating a
    /// runway next, everyone else last. The rank decides only who yields (<see cref="ResolveCrossing"/>'s Rule 1,
    /// <see cref="WouldDeadlock"/>); it never takes away a ranked aircraft's own closing check against what lies in its path.
    /// </summary>
    private enum RunwayRank
    {
        /// <summary>Neither using nor vacating a runway.</summary>
        Rest,

        /// <summary>
        /// Leaving a runway with its tail still over the pavement or inside the hold line (<see cref="RunwayOccupancy.IsVacating"/>).
        /// </summary>
        Vacating,

        /// <summary>
        /// Taking off, landing, rolling out, lined up or lining up on the pavement (<see cref="RunwayOccupancy.OccupiesSurface"/>).
        /// </summary>
        RunwayUser,
    }

    /// <summary>
    /// Fraction of the source's removal window of delivery silence after which a coasting live surface track stops
    /// being an obstacle (the feed host removes it at the full window). A fraction rather than a fixed grace so the
    /// ghost threshold always lands between the coast threshold and removal for every source (ASDE-X 36 s, STARS 54 s;
    /// explicitly ended tracks are removed promptly and never linger this long).
    /// </summary>
    public const double ExternalCoastGraceFraction = 0.6;

    private enum MovementState
    {
        Stationary,
        Taxiing,
        Pushing,
        Untracked,

        /// <summary>A live-traffic shadow: moves as the real aircraft did; an obstacle to everyone, never a subject.</summary>
        External,
    }

    private enum PairKind
    {
        Distant,
        Stationary,
        Pushback,
        SameEdgeTrailing,
        SameEdgeHeadOn,
        Converging,
        Crossing,
    }

    /// <summary>
    /// Detect ground conflicts and set GroundSpeedLimit on affected aircraft.
    /// Clears all limits first, then classifies each pair into exactly one
    /// <see cref="PairKind"/> and runs the corresponding resolution.
    /// </summary>
    public static void ApplySpeedLimits(
        List<AircraftState> aircraft,
        AirportGroundLayout? layout,
        double deltaSeconds = 0,
        Action<string>? diagnosticLog = null
    )
    {
        _tugRunContinuations = new Dictionary<AircraftState, IReadOnlyList<TugMove>>(ReferenceEqualityComparer.Instance);
        _runwayRanks = new Dictionary<AircraftState, RunwayRank>(ReferenceEqualityComparer.Instance);
        _crossingStopCurveCarried = new HashSet<AircraftState>(ReferenceEqualityComparer.Instance);
        _crossingStopCurveBoundTravel = new Dictionary<AircraftState, double>(ReferenceEqualityComparer.Instance);
        _brakedLastPass = new HashSet<AircraftState>(ReferenceEqualityComparer.Instance);
        diagnosticLog = BuildDiagnosticSink(diagnosticLog);
        ResetGroundState(aircraft, deltaSeconds);

        var byCallsign = new Dictionary<string, AircraftState>(StringComparer.OrdinalIgnoreCase);
        foreach (AircraftState ac in aircraft)
        {
            byCallsign.TryAdd(ac.Callsign, ac);
        }

        List<(AircraftState Ac, MovementState State, double? MoveDir)> entries = BuildEntries(aircraft, layout, diagnosticLog);

        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Ac.Ground.ConflictBreakRemainingSeconds > 0)
            {
                continue;
            }

            for (int j = i + 1; j < entries.Count; j++)
            {
                ResolvePair(entries[i], entries[j], byCallsign, layout, diagnosticLog);
            }
        }

        RecordDetectorBraking(aircraft);
        ZeroLapsedBoundClosureTravel(aircraft);
        _tugRunContinuations = null;
        _runwayRanks = null;
        _crossingStopCurveCarried = null;
        _crossingStopCurveBoundTravel = null;
        _brakedLastPass = null;
    }

    /// <summary>
    /// The pass's diagnostic sink: <paramref name="explicitLog"/> alone when no <see cref="DebugSink"/> is installed, the
    /// sink alone when there is no explicit log, and both otherwise (the explicit log first).
    /// </summary>
    private static Action<string>? BuildDiagnosticSink(Action<string>? explicitLog)
    {
        Action<string>? sink = DebugSink;
        if (sink is null)
        {
            return explicitLog;
        }

        return explicitLog is null
            ? sink
            : line =>
            {
                explicitLog(line);
                sink(line);
            };
    }

    /// <summary>
    /// Clears every aircraft's pass-scoped ground state and ages its conflict break, at the top of the pass. A crossing stop
    /// curve carried from the last pass (<see cref="AircraftGroundOps.CrossingStopCurveCarry"/>) moves into the pass's
    /// <see cref="_crossingStopCurveCarried"/>, so only a curve applied again this pass carries it on. The closure travel it
    /// carried (<see cref="AircraftGroundOps.CrossingStopCurveBoundClosureTravelFt"/>) is recorded with it in
    /// <see cref="_crossingStopCurveBoundTravel"/> and stays on the aircraft, for a curve applied again to raise; an aircraft
    /// with no curve carried has it zeroed, as the pass its carry lapsed in already did (<see cref="ZeroLapsedBoundClosureTravel"/>).
    /// The last pass's record that it braked the aircraft
    /// (<see cref="AircraftGroundOps.BrakedByDetectorLastPass"/>) moves the same way into the pass's <see cref="_brakedLastPass"/>,
    /// and <see cref="RecordDetectorBraking"/> writes it again at the end of the pass.
    /// </summary>
    private static void ResetGroundState(List<AircraftState> aircraft, double deltaSeconds)
    {
        for (int i = 0; i < aircraft.Count; i++)
        {
            aircraft[i].Ground.SpeedLimit = null;
            aircraft[i].Ground.AutoYieldTarget = null;
            aircraft[i].Ground.AutoYieldIsFollowing = false;
            if (aircraft[i].Ground.CrossingStopCurveCarry)
            {
                _crossingStopCurveCarried?.Add(aircraft[i]);
                _crossingStopCurveBoundTravel?.TryAdd(aircraft[i], aircraft[i].Ground.CrossingStopCurveBoundClosureTravelFt);
                aircraft[i].Ground.CrossingStopCurveCarry = false;
            }
            else
            {
                aircraft[i].Ground.CrossingStopCurveBoundClosureTravelFt = 0.0;
            }

            if (aircraft[i].Ground.BrakedByDetectorLastPass)
            {
                _brakedLastPass?.Add(aircraft[i]);
                aircraft[i].Ground.BrakedByDetectorLastPass = false;
            }

            if (aircraft[i].Ground.ConflictBreakRemainingSeconds > 0)
            {
                aircraft[i].Ground.ConflictBreakRemainingSeconds = Math.Max(0, aircraft[i].Ground.ConflictBreakRemainingSeconds - deltaSeconds);
            }
        }
    }

    /// <summary>
    /// Records, at the end of the pass, whether it braked each aircraft (<see cref="AircraftGroundOps.BrakedByDetectorLastPass"/>):
    /// the speed limit it set is below the aircraft's ground speed, still the one it had at the start of the pass, since the
    /// pass only sets limits and physics flies them after it. The next pass reads it (<see cref="_brakedLastPass"/>), when the
    /// limit alone no longer shows it: physics clamps the speed to the limit at once.
    /// </summary>
    private static void RecordDetectorBraking(List<AircraftState> aircraft)
    {
        foreach (AircraftState ac in aircraft)
        {
            ac.Ground.BrakedByDetectorLastPass = (ac.Ground.SpeedLimit is { } limitKts) && (limitKts < ac.GroundSpeed);
        }
    }

    /// <summary>
    /// Zeroes, at the end of the pass, the closure travel (<see cref="AircraftGroundOps.CrossingStopCurveBoundClosureTravelFt"/>)
    /// of each aircraft whose crossing stop curve the pass did not carry on (<see cref="AircraftGroundOps.CrossingStopCurveCarry"/>
    /// not set again), so no snapshot taken between passes remembers a travel for a carry that has lapsed.
    /// </summary>
    private static void ZeroLapsedBoundClosureTravel(List<AircraftState> aircraft)
    {
        foreach (AircraftState ac in aircraft)
        {
            if (!ac.Ground.CrossingStopCurveCarry)
            {
                ac.Ground.CrossingStopCurveBoundClosureTravelFt = 0.0;
            }
        }
    }

    /// <summary>
    /// Classifies every aircraft of the pass into its <see cref="MovementState"/> for pair resolution, skipping shadows
    /// that have coasted past the grace period and stamping the external-on-runway flag on the rest.
    /// </summary>
    private static List<(AircraftState Ac, MovementState State, double? MoveDir)> BuildEntries(
        List<AircraftState> aircraft,
        AirportGroundLayout? layout,
        Action<string>? diagnosticLog
    )
    {
        IReadOnlyList<RunwayInfo> runways = layout is not null ? RunwayOccupancy.AirportRunways(layout.AirportId) : [];
        var entries = new List<(AircraftState Ac, MovementState State, double? MoveDir)>();
        for (int i = 0; i < aircraft.Count; i++)
        {
            AircraftState ac = aircraft[i];
            if (!ac.IsOnGround)
            {
                continue;
            }

            if (ac.IsShadow)
            {
                // A coasting surface track past the grace period is a ghost: it must not sweep stops through the movement area.
                AircraftLiveTraffic live = ac.LiveTraffic!;
                if (
                    live.IsCoasting
                    && (live.DeliverySilenceSeconds > ExternalCoastGraceFraction * LiveTraffic.LiveTrafficKinematics.RemovalAfterSeconds(live.Source))
                )
                {
                    continue;
                }

                ac.Ground.ExternalOnRunway =
                    RunwayOccupancy.ClassifyBest(ac, runways, layout) is { } use && RunwayOccupancy.OccupiesSurface(use.Kind);
            }

            if (_runwayRanks is { } ranks)
            {
                ranks[ac] = ComputeRank(ac, layout, runways);
            }

            (MovementState state, double? dir) = Classify(ac);
            entries.Add((ac, state, dir));
            LogClassification(ac, state, dir, diagnosticLog);
        }

        return entries;
    }

    /// <summary>Logs one aircraft's <see cref="MovementState"/>, its hold reason and its current follow lead.</summary>
    private static void LogClassification(AircraftState ac, MovementState state, double? dir, Action<string>? diagnosticLog)
    {
        if (diagnosticLog is null)
        {
            return;
        }

        string holdReason = ac.Ground.Hold switch
        {
            { Kind: HoldKind.GiveWay, YieldTarget: { } t } => $" hold=GiveWay→{t}",
            { Kind: HoldKind.HoldPosition } => " hold=HoldPosition",
            _ => string.Empty,
        };
        TaxiRoute? driven = FollowingPhase.DrivenRouteOf(ac);
        string lead = ac.Phases?.CurrentPhase is FollowingPhase follow ? $", lead={follow.TargetCallsign}" : string.Empty;
        diagnosticLog(
            $"[Classify] {ac.Callsign}: {state}{holdReason}, dir={dir?.ToString("F0") ?? "null"}, gs={ac.GroundSpeed:F1}, "
                + $"phase={ac.Phases?.CurrentPhase?.Name ?? "null"}, "
                + $"route={(driven is null ? "null/null" : $"{driven.CurrentSegmentIndex}/{driven.Segments.Count}")}{lead}"
        );
    }

    /// <summary>The pair as its resolution sees it: both aircraft's entries, their distance (ft) and their <see cref="PairKind"/>.</summary>
    private readonly record struct PairView(
        (AircraftState Ac, MovementState State, double? MoveDir) A,
        (AircraftState Ac, MovementState State, double? MoveDir) B,
        double DistFt,
        PairKind Kind
    );

    /// <summary>
    /// Resolves one pair of the pass: skips it when the second aircraft is breaking out, when the pair is beyond
    /// <see cref="SearchRangeNm"/>, or when both sides are in the other's follow lead chain
    /// (<see cref="LeadChainExemptionOf"/>); otherwise classifies it, logs it, and runs its handler.
    /// </summary>
    private static void ResolvePair(
        (AircraftState Ac, MovementState State, double? MoveDir) a,
        (AircraftState Ac, MovementState State, double? MoveDir) b,
        Dictionary<string, AircraftState> byCallsign,
        AirportGroundLayout? layout,
        Action<string>? diagnosticLog
    )
    {
        if (b.Ac.Ground.ConflictBreakRemainingSeconds > 0)
        {
            return;
        }

        double distNm = GeoMath.DistanceNm(a.Ac.Position, b.Ac.Position);
        if (distNm > SearchRangeNm)
        {
            return;
        }

        (LeadChainExemption exemption, string? closing) = LeadChainExemptionOf(a.Ac, a.State, b.Ac, b.State, byCallsign);
        if (exemption == LeadChainExemption.Both)
        {
            return;
        }

        double distFt = distNm * FtPerNm;
        PairKind kind = ClassifyPair(a.Ac, a.State, b.Ac, b.State, layout);
        var pair = new PairView(a, b, distFt, kind);

        LogPairVisibility(pair, diagnosticLog);
        OneSidedPair? oneSided = BuildOneSidedPair(exemption, closing, a, b, diagnosticLog);

        AircraftState? releasedPusher = ApplyPairResolution(pair, layout, diagnosticLog);
        oneSided?.Finish(distFt, releasedPusher, diagnosticLog);
    }

    /// <summary>
    /// Makes the pair visible to the sink: the controller-supplied GIVEWAY relationship first, then the classification
    /// line. The pair resolution is still Stationary-driven (one aircraft is held, so it already classifies as
    /// Stationary), but the operator sees who is yielding to whom rather than an anonymous "Stationary" pair.
    /// </summary>
    private static void LogPairVisibility(PairView pair, Action<string>? diagnosticLog)
    {
        AircraftState acA = pair.A.Ac;
        AircraftState acB = pair.B.Ac;
        if (acA.Ground.Hold is { } ha && ha.IsGiveWayFor(acB.Callsign))
        {
            diagnosticLog?.Invoke($"[Pair] ControllerGiveWay {acA.Callsign}→{acB.Callsign}");
        }
        else if (acB.Ground.Hold is { } hb && hb.IsGiveWayFor(acA.Callsign))
        {
            diagnosticLog?.Invoke($"[Pair] ControllerGiveWay {acB.Callsign}→{acA.Callsign}");
        }

        diagnosticLog?.Invoke($"[Pair] {acA.Callsign}({pair.A.State})+{acB.Callsign}({pair.B.State}): dist={pair.DistFt:F0}ft → {pair.Kind}");
    }

    /// <summary>
    /// The pair's one-sided view when the lead-chain exemption limits only one side (<see cref="OneSidedPair"/>): a lead
    /// closing on its follower is limited as in any other pair, and the follower keeps what it had before it. Null for
    /// every other exemption.
    /// </summary>
    private static OneSidedPair? BuildOneSidedPair(
        LeadChainExemption exemption,
        string? closing,
        (AircraftState Ac, MovementState State, double? MoveDir) a,
        (AircraftState Ac, MovementState State, double? MoveDir) b,
        Action<string>? diagnosticLog
    )
    {
        OneSidedPair? oneSided = exemption switch
        {
            LeadChainExemption.A => OneSidedPair.Before(a.Ac, b.Ac),
            LeadChainExemption.B => OneSidedPair.Before(b.Ac, a.Ac),
            _ => null,
        };
        if (oneSided is { } closingPair)
        {
            diagnosticLog?.Invoke(
                $"  [LeadChain] {closingPair.Lead.Callsign} closing on its follower {closingPair.Follower.Callsign} ({closing}): "
                    + $"only {closingPair.Lead.Callsign} is limited"
            );
        }

        return oneSided;
    }

    /// <summary>Runs the handler of the pair's <see cref="PairKind"/>, returning the pusher it released, if any.</summary>
    private static AircraftState? ApplyPairResolution(PairView pair, AirportGroundLayout? layout, Action<string>? diagnosticLog)
    {
        AircraftState? releasedPusher = null;
        switch (pair.Kind)
        {
            case PairKind.Distant:
            case PairKind.Stationary:
                break;

            case PairKind.Pushback:
                // Pushback is its own world — the dedicated buffer logic is
                // sufficient; we don't run closing/head-on on top.
                releasedPusher = ResolvePushbackYield(
                    pair.A.Ac,
                    pair.A.State,
                    pair.A.MoveDir,
                    pair.B.Ac,
                    pair.B.State,
                    pair.B.MoveDir,
                    pair.DistFt,
                    diagnosticLog
                );
                break;

            case PairKind.SameEdgeTrailing:
                ResolveSameEdgeTrailing(pair.A.Ac, pair.B.Ac, pair.DistFt, diagnosticLog);
                break;

            case PairKind.SameEdgeHeadOn:
                ResolveSameEdgeHeadOn(pair.A.Ac, pair.B.Ac, pair.DistFt, diagnosticLog);
                break;

            case PairKind.Converging:
            {
                AircraftState? convWinner = ResolveConvergence(pair.A.Ac, pair.B.Ac, layout!, diagnosticLog);
                // Closing-proximity is the physical-overlap safety net, but at a true
                // merge (both routes onto the shared node's lane) there is no lateral
                // room for the wingspan bypass to open — applied symmetrically it pins
                // BOTH aircraft to zero (a deadlock). When convergence has chosen a
                // winner and both would stop, hold only the yielder so the winner (the
                // merge-order leader, nearer the shared node) proceeds — mirroring
                // ResolveCrossing's one-holds-one-goes. Head-on is skipped: convergence
                // already made the pair-level decision.
                ApplyConvergenceClosing(
                    pair.A.Ac,
                    pair.A.State,
                    pair.A.MoveDir,
                    pair.B.Ac,
                    pair.B.State,
                    pair.B.MoveDir,
                    pair.DistFt,
                    convWinner,
                    diagnosticLog
                );
                break;
            }

            case PairKind.Crossing:
                ResolveCrossing(pair.A.Ac, pair.A.State, pair.B.Ac, pair.B.State, pair.DistFt, layout is not null, diagnosticLog);
                break;
        }

        return releasedPusher;
    }

    /// <summary>
    /// Returns true if <paramref name="subject"/> can proceed without conflicting
    /// with <paramref name="reference"/>.
    /// </summary>
    public static bool IsClearOf(AircraftState subject, AircraftState reference, AirportGroundLayout? layout)
    {
        double distNm = GeoMath.DistanceNm(subject.Position, reference.Position);
        double distFt = distNm * FtPerNm;

        (MovementState refState, double? _) = Classify(reference);

        if (refState == MovementState.Pushing)
        {
            return distFt > PushbackBufferFt;
        }

        (MovementState subState, double? _) = Classify(subject);
        if (layout is not null && subState == MovementState.Taxiing && refState == MovementState.Taxiing)
        {
            return !ShareUpcomingNode(subject, reference);
        }

        if (refState == MovementState.Stationary)
        {
            (MovementState _, double? subDir) = Classify(subject);
            if (subDir is null || subject.GroundSpeed <= 0)
            {
                return true;
            }

            (double _, double refTrailDist) = GetSeparation(reference, subject);
            if (distFt > refTrailDist)
            {
                return true;
            }

            double bearing = GeoMath.BearingTo(subject.Position, reference.Position);
            return HeadingDifference(subDir.Value, bearing) >= 90;
        }

        return distFt > OppositeStopDistanceFt;
    }

    // --- Classification ---

    /// <summary>
    /// True when the aircraft is genuinely at rest: near-stopped AND not commanding forward speed. A phase
    /// that is commanding speed is a mover however slowly it happens to be rolling — <c>LineUpPhase</c>
    /// drives at a 2 kt lineup speed, below <see cref="HeldStationarySpeedKts"/>, so judging it by
    /// instantaneous speed alone made a lining-up aircraft a parked obstacle every time a conflict pin had
    /// just zeroed its speed, and it crept through the aircraft ahead one sub-tick at a time (#409).
    /// A held or lined-up-and-waiting aircraft pins <c>TargetSpeed</c> to 0, so it still reads as at rest.
    /// </summary>
    private static bool IsAtRest(AircraftState ac) => IsAtRest(ac.GroundSpeed, ac.Targets.TargetSpeed);

    private static bool IsAtRest(double groundSpeedKts, double? targetSpeedKts) => (groundSpeedKts < HeldStationarySpeedKts) && !(targetSpeedKts > 0);

    private static (MovementState State, double? MoveDirection) Classify(AircraftState ac)
    {
        if (ac.IsShadow)
        {
            // A stopped shadow is a passable obstacle (wingtip clearance applies); a moving one is External.
            return ac.GroundSpeed > 0 ? (MovementState.External, ac.TrueHeading.Degrees) : (MovementState.Stationary, null);
        }

        string? phaseName = ac.Phases?.CurrentPhase?.Name;

        // An aircraft on a tug (push or pull) is never Stationary, at whatever speed: the move is running and only
        // a limit is holding it. Physics nulls Targets.TargetSpeed the moment IAS reaches a zero limit, so the
        // rest-based gates below would call a tug move stopped by the outline rule Stationary on the very next
        // pass; the pair then drops out of resolution, no limit is issued, the tug accelerates for one sub-tick,
        // and the aircraft inches into whatever it was stopped for at about 0.2 ft/s. A push never hit this —
        // Ground.PushbackTrueHeading classifies it Pushing below whatever its speed — but a pull clears that
        // heading and so fell through to the rest gates. Same shape as #407 and #409 below: a phase that is
        // actively driving the aircraft is a mover, not an obstacle. A controller hold (IsImmobile) is different
        // and still counts: PushbackPhase.OnTick brakes the tow to a stop at the towbar rate and returns while it
        // is in force, so a held tug move is a mover until it is at rest and a passable obstacle after that.
        bool underTug = ac.Phases?.CurrentPhase is PushbackPhase { Status: Phases.PhaseStatus.Active };

        // A stationary-named phase only counts as Stationary while the aircraft is
        // actually at rest. LineUpPhase ("LiningUp") in particular actively drives the
        // aircraft from the hold-short onto the runway centerline — classifying it as
        // parked put a lining-up/LUAW pair into the no-op Stationary bucket, and the
        // lining-up aircraft drove straight through the one holding in position
        // (issue #409). Same shape as the held-but-moving gate below (#407).
        if (!underTug && IsStationaryPhase(phaseName) && IsAtRest(ac))
        {
            return (MovementState.Stationary, null);
        }

        // Controller-held aircraft (HOLDPOSITION or GIVEWAY) are functionally parked
        // until released — they won't move until the resume condition fires
        // (FlightPhysics.UpdateGiveWayResume for GIVEWAY geometry, or operator RES
        // for either). Treat them as Stationary so other aircraft can pass laterally
        // beside them when wingspan clearance allows. Without this, a held aircraft
        // on a taxiway would block every passing aircraft via closing-proximity.
        // Only while actually near-stationary, though: during the deceleration window
        // (or any state that leaves a held aircraft rolling) it must keep participating
        // as a mover, or a held head-on pair drops out of resolution entirely and the
        // aircraft drive through each other (issue #407).
        if ((ac.Ground.IsImmobile) && IsAtRest(ac))
        {
            return (MovementState.Stationary, null);
        }

        if (ac.Ground.PushbackTrueHeading is { } pushHdg)
        {
            return (MovementState.Pushing, pushHdg.Degrees);
        }

        if (FollowingPhase.DrivenRouteOf(ac)?.CurrentSegment is not null)
        {
            return (MovementState.Taxiing, ac.TrueHeading.Degrees);
        }

        // A stopped aircraft with no route is an obstacle — unless it is commanding forward speed, or is on a
        // tug, in which case it is a mover that a conflict pin is holding at zero this instant, and it keeps its
        // heading-based closing direction so the pin survives the next sub-tick.
        if (!underTug && (ac.GroundSpeed <= 0) && !(ac.Targets.TargetSpeed > 0))
        {
            return (MovementState.Stationary, null);
        }

        return (MovementState.Untracked, ac.TrueHeading.Degrees);
    }

    /// <summary>
    /// Whether <paramref name="other"/> is in <paramref name="follower"/>'s <c>FOLLOWG</c> lead chain: its lead (the aircraft its
    /// <see cref="FollowingPhase.TargetCallsign"/> names), that lead's lead while it is following too, and so on, looked up in
    /// <paramref name="byCallsign"/>. The walk takes at most one step per aircraft in <paramref name="byCallsign"/>, so a chain
    /// that comes back on itself ends. A sibling follower of the same lead is not in the chain. What the detector does with such a
    /// pair is <see cref="LeadChainExemptionOf"/>.
    /// </summary>
    private static bool IsInLeadChainOf(AircraftState follower, AircraftState other, Dictionary<string, AircraftState> byCallsign)
    {
        AircraftState? current = follower;
        for (int step = 0; (step < byCallsign.Count) && (current?.Phases?.CurrentPhase is FollowingPhase follow); step++)
        {
            if (string.Equals(follow.TargetCallsign, other.Callsign, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = byCallsign.GetValueOrDefault(follow.TargetCallsign);
        }

        return false;
    }

    /// <summary>Which side of a pair the lead-chain exemption leaves unlimited (<see cref="LeadChainExemptionOf"/>).</summary>
    private enum LeadChainExemption
    {
        None,
        A,
        B,
        Both,
    }

    /// <summary>
    /// Which of <paramref name="a"/> and <paramref name="b"/> the detector leaves unlimited against the other because one is in the
    /// other's <c>FOLLOWG</c> lead chain (<see cref="IsInLeadChainOf"/>). A follower is exempt toward every aircraft in its lead
    /// chain; a lead in that chain is exempt from its follower only while it is not closing on it
    /// (<see cref="ClosingOnFollower"/>, returned as the closing clause for the log). Both when neither is limited, so the pair is
    /// not resolved at all; None when neither is in the other's chain.
    /// </summary>
    private static (LeadChainExemption Exemption, string? Closing) LeadChainExemptionOf(
        AircraftState a,
        MovementState stateA,
        AircraftState b,
        MovementState stateB,
        Dictionary<string, AircraftState> byCallsign
    )
    {
        bool aFollowsB = IsInLeadChainOf(a, b, byCallsign);
        bool bFollowsA = IsInLeadChainOf(b, a, byCallsign);
        string? aClosing = bFollowsA ? ClosingOnFollower(a, stateA, b) : null;
        string? bClosing = aFollowsB ? ClosingOnFollower(b, stateB, a) : null;
        bool aExempt = aFollowsB || (bFollowsA && (aClosing is null));
        bool bExempt = bFollowsA || (aFollowsB && (bClosing is null));
        LeadChainExemption exemption = (aExempt, bExempt) switch
        {
            (true, true) => LeadChainExemption.Both,
            (true, false) => LeadChainExemption.A,
            (false, true) => LeadChainExemption.B,
            _ => LeadChainExemption.None,
        };
        return (exemption, aClosing ?? bClosing);
    }

    /// <summary>
    /// How <paramref name="lead"/> is closing on <paramref name="follower"/>, for the log, or null when it is not: pushing with
    /// its push track toward it (<see cref="FollowingPhase.LeadTracksTowardFollower"/>), or coming back along its own trail
    /// toward it (<see cref="ReEnteringTrailToward"/>). A push away from the follower is not closing on it.
    /// </summary>
    private static string? ClosingOnFollower(AircraftState lead, MovementState leadState, AircraftState follower)
    {
        if (leadState == MovementState.Pushing)
        {
            return FollowingPhase.LeadTracksTowardFollower(follower, lead) ? "pushing toward it" : null;
        }

        return ReEnteringTrailToward(lead, follower);
    }

    /// <summary>
    /// How <paramref name="lead"/> is re-entering its own <see cref="AircraftGroundOps.TaxiEdgeTrail"/> with
    /// <paramref name="follower"/> standing on a trail edge, for the log, or null when it is not. It is retracing its trail when
    /// the edge it is on (<see cref="TaxiEdgeLocator.DrivenEdgeUnder"/>) is a trail edge older than the newest
    /// (<see cref="IsRetracing"/>); it is coming back over the fillet arc it came round onto its newest trail edge
    /// (<see cref="IsReturningOverArc"/>); or, on that newest edge itself (<see cref="IsOnNewestEdge"/>), it has turned about when
    /// its track points back toward the node it entered the edge from (<see cref="IsTurnedAboutOnNewestEdge"/>). A lead on an edge
    /// its trail does not hold yet is on new ground; it, one rounding a fillet arc outbound, and one whose follower waits on a
    /// taxiway the lead never used are not re-entering.
    /// </summary>
    private static string? ReEnteringTrailToward(AircraftState lead, AircraftState follower)
    {
        TaxiEdgeTrail trail = lead.Ground.TaxiEdgeTrail;
        if ((lead.Ground.Layout is not { } layout) || (trail.Newest is not { } newest) || !IsOnTrail(layout, follower, trail))
        {
            return null;
        }

        GroundEdge? leadEdge = TaxiEdgeLocator.DrivenEdgeUnder(layout, lead.Position, (newest.NodeA, newest.NodeB));
        if (IsRetracing(trail, leadEdge))
        {
            return "retracing its trail";
        }

        if (IsReturningOverArc(layout, lead, newest, trail))
        {
            return "coming back over the fillet arc it came round";
        }

        bool onNewest = IsOnNewestEdge(layout, lead, newest, leadEdge);
        return (onNewest && IsTurnedAboutOnNewestEdge(layout, lead, newest)) ? "turned about on its newest trail edge" : null;
    }

    /// <summary>
    /// Whether <paramref name="lead"/> stands on <paramref name="newest"/>, its newest trail edge. With a driven route whose
    /// current segment is a straight edge, it is on that edge: just past a fillet arc whose radius shrank, the locator still
    /// finds the edge before the arc under it. With none, or while that route turns it about on the newest edge
    /// (<see cref="IsTurningAboutOn"/>), the edge found under it, <paramref name="leadEdge"/>, is the newest edge, or it is no
    /// farther from the newest edge than from the one found — at the node the two share it stands on both. On an edge its
    /// trail does not hold yet, it is on new ground.
    /// </summary>
    private static bool IsOnNewestEdge(AirportGroundLayout layout, AircraftState lead, TaxiTrailEdge newest, GroundEdge? leadEdge)
    {
        TaxiRoute? route = FollowingPhase.DrivenRouteOf(lead);
        if (!IsTurningAboutOn(route, newest) && (route?.CurrentSegment?.Edge.Edge is GroundEdge driving))
        {
            return newest.Is(driving);
        }

        if (leadEdge is null)
        {
            return false;
        }

        return newest.Is(leadEdge)
            || (
                (newest.Resolve(layout) is { } newestEdge)
                && (
                    GeoMath.DistanceToSegmentFt(lead.Position, newestEdge.Nodes[0].Position, newestEdge.Nodes[1].Position)
                    <= GeoMath.DistanceToSegmentFt(lead.Position, leadEdge.Nodes[0].Position, leadEdge.Nodes[1].Position)
                )
            );
    }

    /// <summary>
    /// Whether <paramref name="route"/> is still turning its aircraft about from the far end of the edge it stood mid-way along
    /// (<see cref="TaxiTurnAboutShape.FromFarEnd"/>) toward a node of <paramref name="newest"/>: its current segment is then the
    /// drive back along that edge, or the edge on from the target beyond it, never the edge the aircraft is driving.
    /// </summary>
    private static bool IsTurningAboutOn(TaxiRoute? route, TaxiTrailEdge newest) =>
        (route is { PendingTurnAboutShape: TaxiTurnAboutShape.FromFarEnd, TurnAboutTargetNodeId: int target }) && newest.Touches(target);

    /// <summary>
    /// Whether <paramref name="follower"/> stands on an edge of <paramref name="trail"/> (<see cref="TaxiEdgeLocator.DrivenEdgeUnder"/>).
    /// </summary>
    private static bool IsOnTrail(AirportGroundLayout layout, AircraftState follower, TaxiEdgeTrail trail)
    {
        (int NodeA, int NodeB)? lastEdge = follower.Ground.TaxiEdgeTrail.Newest is { } newest ? (newest.NodeA, newest.NodeB) : null;
        if (TaxiEdgeLocator.DrivenEdgeUnder(layout, follower.Position, lastEdge) is not { } followerEdge)
        {
            return false;
        }

        foreach (TaxiTrailEdge driven in trail.Edges)
        {
            if (driven.Is(followerEdge))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="leadEdge"/>, the edge the lead is on, is an edge of <paramref name="trail"/> older than its newest:
    /// driven earlier and come back to. The edge just before the newest counts only when the two share no node, a fillet arc or
    /// an edge driven between two records lying between them: where they meet at a junction node, a lead at that node can read
    /// as on either, and one that has driven back past it is on the older edge for at most the second until the next record,
    /// which puts that edge further back in the trail. False when the lead is on no edge.
    /// </summary>
    private static bool IsRetracing(TaxiEdgeTrail trail, GroundEdge? leadEdge)
    {
        if (leadEdge is null)
        {
            return false;
        }

        IReadOnlyList<TaxiTrailEdge> edges = trail.Edges;
        int beforeNewest = edges.Count - 2;
        for (int i = 0; i < beforeNewest; i++)
        {
            if (edges[i].Is(leadEdge))
            {
                return true;
            }
        }

        return (beforeNewest >= 0) && edges[beforeNewest].Is(leadEdge) && !SharesANode(edges[beforeNewest], edges[^1]);
    }

    private static bool SharesANode(TaxiTrailEdge a, TaxiTrailEdge b) => b.Touches(a.NodeA) || b.Touches(a.NodeB);

    /// <summary>
    /// Whether <paramref name="lead"/> is coming back over the fillet arc it came round onto <paramref name="newest"/>, its
    /// newest trail edge: it is on an arc ending at the node it entered that edge from (<see cref="ArcFromEntryUnder"/>) and, at
    /// its other end, at a node of an older edge of <paramref name="trail"/>, its track (its pushback track on a tug, else its
    /// heading) within 90° of the arc's direction toward that other end. An arc rounded outbound meets the newest edge at its far
    /// end, and one onto new ground ends at no older trail node, so neither is coming back.
    /// </summary>
    private static bool IsReturningOverArc(AirportGroundLayout layout, AircraftState lead, TaxiTrailEdge newest, TaxiEdgeTrail trail)
    {
        if (!layout.Nodes.TryGetValue(newest.EntryNodeId, out GroundNode? entry) || (ArcFromEntryUnder(layout, lead, entry, newest) is not { } onArc))
        {
            return false;
        }

        GroundNode far = onArc.Arc.OtherNode(entry);
        if (!IsNodeOfAnOlderEdge(trail, far.Id))
        {
            return false;
        }

        double alongDeg = onArc.Arc.ToBezier().TangentBearing(onArc.T);
        double towardFarDeg = (far.Id == onArc.Arc.Nodes[0].Id) ? (alongDeg + 180.0) % 360.0 : alongDeg;
        return TaxiEdgeTrail.TracksWithin90Of(lead, towardFarDeg);
    }

    /// <summary>
    /// The fillet arc ending at <paramref name="entry"/>, the node <paramref name="lead"/> entered <paramref name="newest"/> from,
    /// that the lead is on, with the parameter of the arc's curve nearest it: its driven route's current segment when that is such
    /// an arc (close to the arc's end a straight edge there can be nearer than the curve), else the arc
    /// <see cref="TaxiEdgeLocator.FilletArcEndingAt"/> finds under it. Null when it is on none.
    /// </summary>
    private static (GroundArc Arc, double T)? ArcFromEntryUnder(
        AirportGroundLayout layout,
        AircraftState lead,
        GroundNode entry,
        TaxiTrailEdge newest
    )
    {
        if (
            (FollowingPhase.DrivenRouteOf(lead)?.CurrentSegment?.Edge.Edge is GroundArc { IsRunwayCenterline: false } driving)
            && ((driving.Nodes[0].Id == entry.Id) || (driving.Nodes[1].Id == entry.Id))
        )
        {
            return (driving, TaxiEdgeLocator.ClosestT(driving, lead.Position));
        }

        return TaxiEdgeLocator.FilletArcEndingAt(layout, lead.Position, entry, (newest.NodeA, newest.NodeB));
    }

    /// <summary>Whether <paramref name="nodeId"/> is an end node of an edge of <paramref name="trail"/> older than its newest.</summary>
    private static bool IsNodeOfAnOlderEdge(TaxiEdgeTrail trail, int nodeId)
    {
        IReadOnlyList<TaxiTrailEdge> edges = trail.Edges;
        for (int i = 0; i < (edges.Count - 1); i++)
        {
            if (edges[i].Touches(nodeId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="lead"/>, on <paramref name="newest"/>, its newest trail edge, has its track (its pushback track on
    /// a tug, else its heading) within 90° of the bearing to the node it entered that edge from
    /// (<see cref="TaxiTrailEdge.EntryNodeId"/>). Never while it rounds a fillet arc (<see cref="IsRoundingArc"/>), where the
    /// turn swings its heading back past that node before it reaches new ground.
    /// </summary>
    private static bool IsTurnedAboutOnNewestEdge(AirportGroundLayout layout, AircraftState lead, TaxiTrailEdge newest)
    {
        if (!layout.Nodes.TryGetValue(newest.EntryNodeId, out GroundNode? entry) || IsRoundingArc(layout, lead, newest))
        {
            return false;
        }

        return TaxiEdgeTrail.TracksWithin90Of(lead, GeoMath.BearingTo(lead.Position, entry.Position));
    }

    /// <summary>
    /// Whether <paramref name="lead"/> is on a fillet arc: its driven route's current segment is a <see cref="GroundArc"/>, or it
    /// stands on an arc meeting <paramref name="newest"/> (<see cref="TaxiEdgeLocator.FilletArcRounding"/>).
    /// </summary>
    private static bool IsRoundingArc(AirportGroundLayout layout, AircraftState lead, TaxiTrailEdge newest) =>
        (FollowingPhase.DrivenRouteOf(lead)?.CurrentSegment?.Edge.Edge is GroundArc)
        || (TaxiEdgeLocator.FilletArcRounding(layout, lead.Position, (newest.NodeA, newest.NodeB)) is not null);

    /// <summary>
    /// A pair in which only the lead is limited (<see cref="LeadChainExemptionOf"/>): both aircraft's limits as they stood before
    /// the pair was resolved. <see cref="Before"/> clears both, so what the pair's resolution writes to each is read on its own in
    /// <see cref="Finish"/>, whatever earlier pairs in the pass had already set.
    /// </summary>
    private readonly record struct OneSidedPair(AircraftState Follower, AircraftState Lead, GroundLimits FollowerBefore, GroundLimits LeadBefore)
    {
        internal static OneSidedPair Before(AircraftState follower, AircraftState lead)
        {
            var pair = new OneSidedPair(follower, lead, GroundLimits.Of(follower), GroundLimits.Of(lead));
            GroundLimits.None.RestoreTo(follower);
            GroundLimits.None.RestoreTo(lead);
            return pair;
        }

        /// <summary>
        /// Puts the follower's limits back as they were, since it is exempt toward its lead chain, and merges what the pair's
        /// resolution wrote to the lead into the lead's earlier limits (<see cref="GroundLimits.Merge"/>). When the resolution
        /// limited the follower and not the lead, the lead yields to its follower instead (<see cref="TransferredLimitKts"/>), so
        /// a follower that cannot yield never lets its closing lead drive into it — unless the resolution released the lead,
        /// <paramref name="releasedPusher"/> being a push it let continue past the follower holding for it
        /// (<see cref="TryResolveGiveWayToPushback"/>).
        /// </summary>
        internal void Finish(double distFt, AircraftState? releasedPusher, Action<string>? diagnosticLog)
        {
            var followerWritten = GroundLimits.Of(Follower);
            FollowerBefore.RestoreTo(Follower);
            if (
                (Lead.Ground.SpeedLimit is null)
                && (followerWritten.SpeedLimit is { } movedKts)
                && !ReferenceEquals(releasedPusher, Lead)
                && (TransferredLimitKts(movedKts, distFt) is { } leadKts)
            )
            {
                diagnosticLog?.Invoke($"  [LeadChain] {Lead.Callsign} yields to its follower {Follower.Callsign}: limit={leadKts:F1}");
                ApplyMinLimit(
                    Lead,
                    leadKts,
                    new LimitCause("closing on its follower", Follower, distFt, distFt - DefaultStopDistanceFt, StopClosure.None)
                );
                Lead.Ground.AutoYieldTarget = Follower.Callsign;
                Lead.Ground.AutoYieldIsFollowing = false;
            }

            GroundLimits.Merge(LeadBefore, GroundLimits.Of(Lead)).RestoreTo(Lead);
        }

        /// <summary>
        /// The limit (kt) the lead takes for the <paramref name="movedKts"/> the resolution gave its follower: a stop stays a stop;
        /// any other limit carries over, and <see cref="ApplyMinLimit"/> raises it to the speed the lead can still brake down from
        /// over the next detector pass (<see cref="FlooredLimitKts"/>). Null for a trail match (<see cref="TrailMatchKts"/>: the
        /// lead's own ground speed, floored for the follower), which only holds the follower behind the lead and asks nothing of
        /// the lead.
        /// </summary>
        private double? TransferredLimitKts(double movedKts, double distFt)
        {
            if (movedKts <= 0.0)
            {
                return 0.0;
            }

            if (movedKts == TrailMatchKts(Follower, Lead, distFt))
            {
                return null;
            }

            return movedKts;
        }
    }

    /// <summary>The limit and yield target an aircraft carries, put back on it after a pair it is exempt from.</summary>
    private readonly record struct GroundLimits(double? SpeedLimit, string? AutoYieldTarget, bool AutoYieldIsFollowing)
    {
        /// <summary>No limit and no yield target, as every aircraft starts a detector pass.</summary>
        internal static readonly GroundLimits None = new(null, null, false);

        internal static GroundLimits Of(AircraftState ac) => new(ac.Ground.SpeedLimit, ac.Ground.AutoYieldTarget, ac.Ground.AutoYieldIsFollowing);

        /// <summary>
        /// The lower of the two limits (no limit being the highest), with the yield target that goes with it — or, when that side
        /// names none, the other side's; <paramref name="earlier"/> wins a tie.
        /// </summary>
        internal static GroundLimits Merge(GroundLimits earlier, GroundLimits pair)
        {
            bool pairWins = (pair.SpeedLimit is { } pairKts) && ((earlier.SpeedLimit is not { } earlierKts) || (pairKts < earlierKts));
            (GroundLimits winner, GroundLimits other) = pairWins ? (pair, earlier) : (earlier, pair);
            GroundLimits yieldSide = winner.AutoYieldTarget is not null ? winner : other;
            return new GroundLimits(winner.SpeedLimit, yieldSide.AutoYieldTarget, yieldSide.AutoYieldIsFollowing);
        }

        internal void RestoreTo(AircraftState ac)
        {
            ac.Ground.SpeedLimit = SpeedLimit;
            ac.Ground.AutoYieldTarget = AutoYieldTarget;
            ac.Ground.AutoYieldIsFollowing = AutoYieldIsFollowing;
        }
    }

    private static PairKind ClassifyPair(AircraftState a, MovementState stateA, AircraftState b, MovementState stateB, AirportGroundLayout? layout)
    {
        if (stateA == MovementState.Stationary && stateB == MovementState.Stationary)
        {
            return PairKind.Stationary;
        }

        if (stateA == MovementState.Pushing || stateB == MovementState.Pushing)
        {
            return PairKind.Pushback;
        }

        if (layout is not null && stateA == MovementState.Taxiing && stateB == MovementState.Taxiing)
        {
            TaxiRouteSegment? segA = FollowingPhase.DrivenRouteOf(a)?.CurrentSegment;
            TaxiRouteSegment? segB = FollowingPhase.DrivenRouteOf(b)?.CurrentSegment;
            if (segA is not null && segB is not null)
            {
                bool sameEdge =
                    (segA.FromNodeId == segB.FromNodeId && segA.ToNodeId == segB.ToNodeId)
                    || (segA.FromNodeId == segB.ToNodeId && segA.ToNodeId == segB.FromNodeId);

                if (sameEdge)
                {
                    return segA.ToNodeId == segB.ToNodeId ? PairKind.SameEdgeTrailing : PairKind.SameEdgeHeadOn;
                }
            }

            TaxiRoute? routeA = FollowingPhase.DrivenRouteOf(a);
            TaxiRoute? routeB = FollowingPhase.DrivenRouteOf(b);
            if (routeA is not null && routeB is not null && FindSharedUpcomingNode(routeA, routeB) is not null)
            {
                return PairKind.Converging;
            }
        }

        return PairKind.Crossing;
    }

    // --- Conflict resolution ---

    private static void ResolveSameEdgeTrailing(AircraftState a, AircraftState b, double distFt, Action<string>? diagnosticLog)
    {
        if ((FollowingPhase.DrivenRouteOf(a)?.CurrentSegment is not { } segA) || (FollowingPhase.DrivenRouteOf(b)?.CurrentSegment is not { } segB))
        {
            return;
        }

        // The two are nose-to-tail on one edge, so the one to cap is whichever has less progress along it
        // (AlongEdgeNm); ordering them any other way caps the aircraft in front.
        double alongA = AlongEdgeNm(a, segA);
        double alongB = AlongEdgeNm(b, segB);

        diagnosticLog?.Invoke(
            $"  [SameEdgeTrailing] edge={segA.FromNodeId}→{segA.ToNodeId}: {a.Callsign} along={alongA:F4}nm, {b.Callsign} along={alongB:F4}nm"
        );

        if (alongA < alongB)
        {
            ApplyTrailLimit(a, b, distFt);
            a.Ground.AutoYieldTarget = b.Callsign;
            a.Ground.AutoYieldIsFollowing = true;
        }
        else
        {
            ApplyTrailLimit(b, a, distFt);
            b.Ground.AutoYieldTarget = a.Callsign;
            b.Ground.AutoYieldIsFollowing = true;
        }
    }

    private static void ResolveSameEdgeHeadOn(AircraftState a, AircraftState b, double distFt, Action<string>? diagnosticLog)
    {
        if (distFt > OppositeStopDistanceFt)
        {
            diagnosticLog?.Invoke($"  [SameEdgeHeadOn] {distFt:F0}ft > {OppositeStopDistanceFt:F0}ft, no action yet");
            return;
        }

        // Pick the holder deterministically so the pair doesn't deadlock. Real
        // ATC would re-route here, but the sim's job is at least to leave one
        // aircraft able to proceed instead of pinning both indefinitely.
        // Holder = aircraft with the higher remaining-segment count (more route
        // left to fly), since it has more reason to wait. Ties broken by callsign.
        TaxiRoute? routeA = FollowingPhase.DrivenRouteOf(a);
        TaxiRoute? routeB = FollowingPhase.DrivenRouteOf(b);
        int remA = routeA is null ? 0 : routeA.Segments.Count - routeA.CurrentSegmentIndex;
        int remB = routeB is null ? 0 : routeB.Segments.Count - routeB.CurrentSegmentIndex;

        AircraftState holder;
        AircraftState mover;
        if (remA != remB)
        {
            holder = remA > remB ? a : b;
            mover = remA > remB ? b : a;
        }
        else
        {
            holder = string.CompareOrdinal(a.Callsign, b.Callsign) >= 0 ? a : b;
            mover = ReferenceEquals(holder, a) ? b : a;
        }

        diagnosticLog?.Invoke($"  [SameEdgeHeadOn] dist={distFt:F0}ft, remA={remA}/{remB}, holder={holder.Callsign}, mover={mover.Callsign}");

        ApplyMinLimit(holder, 0, new LimitCause("same-edge head-on hold", mover, distFt, RoomFt: null, StopClosure.None));
        // mover gets no limit from this layer; closing-proximity (if it fires
        // next tick when the geometry shifts) is the safety net for actual
        // overlap, but the holder being stopped should resolve the standoff.
    }

    /// <summary>
    /// Resolves a Converging pair (routes share an upcoming node from different edges). Slows the
    /// yielder (the aircraft farther from the shared node) and annotates it with the winner as its
    /// auto-yield target. Returns the chosen <b>winner</b> (the merge-order leader, nearer the
    /// shared node) so the caller's closing-proximity safety net can let it proceed instead of
    /// pinning both; returns <c>null</c> when no decision was made (no shared node, or the ETA gate
    /// cleared the crossing).
    /// </summary>
    private static AircraftState? ResolveConvergence(AircraftState a, AircraftState b, AirportGroundLayout layout, Action<string>? diagnosticLog)
    {
        if ((FollowingPhase.DrivenRouteOf(a) is not { } routeA) || (FollowingPhase.DrivenRouteOf(b) is not { } routeB))
        {
            return null;
        }

        int? sharedNodeId = FindSharedUpcomingNode(routeA, routeB);
        if (sharedNodeId is null || !layout.Nodes.TryGetValue(sharedNodeId.Value, out GroundNode? node))
        {
            return null;
        }

        double distA = GeoMath.DistanceNm(a.Position, node.Position);
        double distB = GeoMath.DistanceNm(b.Position, node.Position);
        double distAFt = distA * FtPerNm;
        double distBFt = distB * FtPerNm;
        double conflictDistFt = GeoMath.DistanceNm(a.Position, b.Position) * FtPerNm;

        AircraftState yielder = distA > distB ? a : b;
        AircraftState winner = distA > distB ? b : a;
        double yielderDistFt = Math.Max(distAFt, distBFt);
        double winnerDistFt = Math.Min(distAFt, distBFt);

        // ETA gate: if the nearer aircraft (winner) will clear the shared node well before the
        // farther aircraft (yielder) reaches it, there is no real conflict at the node — braking the
        // yielder only slows it for a crossing that is already clear. Estimate the winner's time to
        // fully clear the node (it must be genuinely moving for this to be trustworthy) and the
        // yielder's time to arrive at an unrestricted taxi speed (so a yielder already capped by this
        // rule does not oscillate).
        if (winner.IndicatedAirspeed > ConvergenceMinWinnerSpeedKts)
        {
            double winnerLengthFt = AircraftLength.ResolveFt(winner.AircraftType);
            double winnerClearSec = (winnerDistFt + winnerLengthFt) / (winner.IndicatedAirspeed * FtPerNm / 3600.0);
            double yielderSpeedKts = Math.Max(yielder.IndicatedAirspeed, ConvergenceNominalTaxiSpeedKts);
            double yielderArriveSec = yielderDistFt / (yielderSpeedKts * FtPerNm / 3600.0);
            if (yielderArriveSec > winnerClearSec + ConvergenceClearanceBufferSec)
            {
                diagnosticLog?.Invoke(
                    $"  [Convergence] shared node={sharedNodeId.Value}: {winner.Callsign} clears in {winnerClearSec:F0}s, "
                        + $"{yielder.Callsign} arrives in {yielderArriveSec:F0}s — no slowdown (clears first)"
                );
                return null;
            }
        }

        double limitSpeed;
        if (conflictDistFt <= DefaultStopDistanceFt)
        {
            limitSpeed = 0;
        }
        else if (conflictDistFt <= ConvergenceSlowdownFt)
        {
            limitSpeed = SlowTaxiSpeedKts;
        }
        else
        {
            double t = Math.Clamp((yielderDistFt - ConvergenceSlowdownFt) / (ConvergenceLookaheadFt - ConvergenceSlowdownFt), 0, 1);
            limitSpeed = SlowTaxiSpeedKts + t * (15.0 - SlowTaxiSpeedKts);
        }

        // A yielder crossing a runway takes no slowdown, only the braking onto the stop once that is inside its own stopping
        // distance, or its ground speed held once that braking has bound (HasCrossingStopCurve, CrossingStopCurveKts), which
        // carries the curve on. The winner is still returned, so the closing safety net holds the yielder alone when both would
        // stop.
        if ((limitSpeed > 0) && HasCrossingStopCurve(yielder))
        {
            double roomFt = conflictDistFt - DefaultStopDistanceFt;
            StopClosure closure = ClosureOf(winner, yielder);
            double? curveKts = CrossingStopCurveKts(yielder, closure, roomFt);
            diagnosticLog?.Invoke(
                $"  [Convergence] shared node={sharedNodeId.Value}: {yielder.Callsign} yields to {winner.Callsign} crossing a runway, "
                    + $"stop curve={curveKts?.ToString("F1") ?? "none"}"
            );
            if (curveKts is { } crossingKts)
            {
                ApplyMinLimit(yielder, crossingKts, new LimitCause(ConvergenceCrossingStopCurveReason, winner, conflictDistFt, roomFt, closure));
            }

            return winner;
        }

        // ApplyMinLimit floors the slowdown at the speed the yielder can still brake from over the next pass, with the room
        // left before DefaultStopDistanceFt; the stop branch (conflict inside it) is a genuine stop and stays at zero.
        diagnosticLog?.Invoke(
            $"  [Convergence] shared node={sharedNodeId.Value}: {a.Callsign} {distAFt:F0}ft away, {b.Callsign} {distBFt:F0}ft away "
                + $"→ {yielder.Callsign} yields to {winner.Callsign}, pairDist={conflictDistFt:F0}ft, limit={limitSpeed:F1}"
        );

        ApplyMinLimit(
            yielder,
            limitSpeed,
            new LimitCause("convergence", winner, conflictDistFt, conflictDistFt - DefaultStopDistanceFt, StopClosure.None)
        );
        yielder.Ground.AutoYieldTarget = winner.Callsign;
        return winner;
    }

    /// <summary>
    /// The speed (kt) <paramref name="aircraft"/> can still brake down from over the next detector pass
    /// (<see cref="DetectorIntervalSeconds"/>), at the rate it is braking at or its category's taxi rate: physics clamps the
    /// speed to <see cref="AircraftGroundOps.SpeedLimit"/> at once, so a limit below this would brake it harder than its brakes
    /// allow. When even that rate's stopping distance no longer fits in the <paramref name="roomFt"/> left before the stop ring,
    /// a floor at it lets the aircraft arrive at the ring still carrying speed it cannot shed there (the ring is an instant
    /// stop), so the floor is taken at the category's max-effort rate instead. For a crossing stop curve the stop counts the
    /// other aircraft's travel toward it while it stops (<paramref name="closure"/>, as <see cref="CrossingStopCurveKts"/> counts
    /// it); for every other limit <paramref name="closure"/> is <see cref="StopClosure.None"/>.
    /// </summary>
    private static double BrakingFloorKts(AircraftState aircraft, double roomFt, StopClosure closure)
    {
        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double decelRate = aircraft.Targets.DesiredDecelRate ?? CategoryPerformance.TaxiDecelRate(category);
        if (ClosingStopOverrunsRoom(aircraft.GroundSpeed, decelRate, closure, roomFt))
        {
            decelRate = CategoryPerformance.ExpediteExitDecelRate(category);
        }

        return aircraft.GroundSpeed - (decelRate * DetectorIntervalSeconds);
    }

    /// <summary>
    /// Closing-proximity safety net for a Converging pair. Runs <see cref="ComputeClosingLimit"/>
    /// both ways, exactly like <see cref="ApplyCrossingChecks"/> (head-on skipped), EXCEPT that
    /// when convergence has chosen a <paramref name="winner"/> and BOTH aircraft would be stopped
    /// (a merge mutual-stop) only the yielder is held, so the winner proceeds through the shared
    /// node instead of both deadlocking at zero. When <paramref name="winner"/> is null (the ETA
    /// gate cleared the crossing, or there is no shared node) it preserves the original symmetric
    /// behavior.
    /// </summary>
    private static void ApplyConvergenceClosing(
        AircraftState a,
        MovementState stateA,
        double? dirA,
        AircraftState b,
        MovementState stateB,
        double? dirB,
        double distFt,
        AircraftState? winner,
        Action<string>? diagnosticLog
    )
    {
        if (winner is null)
        {
            ApplyCrossingChecks(a, stateA, dirA, b, stateB, dirB, distFt, skipHeadOn: true, diagnosticLog);
            return;
        }

        bool winnerIsA = ReferenceEquals(winner, a);
        AircraftState yielder = winnerIsA ? b : a;
        double? winnerDir = winnerIsA ? dirA : dirB;
        MovementState winnerState = winnerIsA ? stateA : stateB;
        double? yielderDir = winnerIsA ? dirB : dirA;
        MovementState yielderState = winnerIsA ? stateB : stateA;

        (double Limit, string Reason, double? RoomFt, StopClosure Closure)? winnerLimit = winnerDir is { } wd
            ? ComputeClosingLimit(winner, wd, yielder, yielderState, distFt, diagnosticLog)
            : null;
        (double Limit, string Reason, double? RoomFt, StopClosure Closure)? yielderLimit = yielderDir is { } yd
            ? ComputeClosingLimit(yielder, yd, winner, winnerState, distFt, diagnosticLog)
            : null;

        if (winnerLimit is { Limit: <= 0 } && yielderLimit is { Limit: <= 0 })
        {
            diagnosticLog?.Invoke($"  [Convergence] merge mutual-stop: {yielder.Callsign} holds, {winner.Callsign} proceeds");
            ApplyMinLimit(yielder, 0, new LimitCause("convergence merge hold", winner, distFt, RoomFt: null, StopClosure.None));
            return;
        }

        if (winnerLimit is { } rw)
        {
            ApplyHolderLimit(winner, rw, yielder, distFt);
        }
        if (yielderLimit is { } ry)
        {
            ApplyHolderLimit(yielder, ry, winner, distFt);
        }
    }

    /// <summary>
    /// One aircraft's side of a pushback pair: the aircraft, the movement classification
    /// <see cref="Classify"/> gave it, and its direction of travel (null when it has none —
    /// a pusher's is the reciprocal of its nose heading). Bundled so the resolution helpers carry
    /// one argument per party instead of three.
    /// </summary>
    private readonly record struct PushbackParty(AircraftState Aircraft, MovementState State, double? Direction);

    /// <summary>
    /// Resolves a pair in which at least one aircraft is pushing back, within
    /// <see cref="PushbackBufferFt"/>. Each side is resolved independently — the two orders are
    /// equivalent (neither reads the other's cap) — except for the pusher-versus-mover case the
    /// independent sides cannot resolve, where <see cref="TryResolveGiveWayToPushback"/> takes the
    /// whole pair (both orderings are offered, and a pair with two pushers or two movers falls
    /// through to the symmetric calls unchanged). Returns the pusher when that give-way took the pair, since the
    /// give-way then settled the pusher's limit itself (no limit when it lets the push continue); else null.
    /// </summary>
    private static AircraftState? ResolvePushbackYield(
        AircraftState a,
        MovementState stateA,
        double? dirA,
        AircraftState b,
        MovementState stateB,
        double? dirB,
        double distFt,
        Action<string>? diagnosticLog
    )
    {
        if (distFt > PushbackBufferFt)
        {
            return null;
        }

        var partyA = new PushbackParty(a, stateA, dirA);
        var partyB = new PushbackParty(b, stateB, dirB);
        if (
            (
                TryResolveGiveWayToPushback(partyA, partyB, distFt, diagnosticLog)
                ?? TryResolveGiveWayToPushback(partyB, partyA, distFt, diagnosticLog)
            ) is
            { } releasedPusher
        )
        {
            return releasedPusher;
        }

        if (AreMutuallyYieldingPushers(partyA, partyB))
        {
            (AircraftState winner, string reason) = ChoosePushbackPairWinner(a, b);
            (PushbackParty loser, PushbackParty goes) = ReferenceEquals(winner, a) ? (partyB, partyA) : (partyA, partyB);
            diagnosticLog?.Invoke(
                $"    [Pushback] {loser.Aircraft.Callsign} holds for pushback {goes.Aircraft.Callsign} (mutual yield, {reason}): dist={distFt:F0}ft"
            );
            ResolvePushbackSide(loser, goes, distFt, diagnosticLog);
            LetTieBreakWinnerGo(goes, loser, distFt, diagnosticLog);
            return null;
        }

        ResolvePushbackSide(partyA, partyB, distFt, diagnosticLog);
        ResolvePushbackSide(partyB, partyA, distFt, diagnosticLog);
        return null;
    }

    /// <summary>
    /// True when two pushers would each take <see cref="PushbackYieldForTraffic"/>'s hard stop for the other: both
    /// are pushing, neither is a parked or held obstacle, and neither has the lateral room to pass the other along its
    /// own push direction. Resolved independently, both would stop and neither would move again.
    /// </summary>
    private static bool AreMutuallyYieldingPushers(PushbackParty a, PushbackParty b) =>
        (a.State == MovementState.Pushing)
        && (b.State == MovementState.Pushing)
        && (a.Direction is { } dirA)
        && (b.Direction is { } dirB)
        && !IsParkedOrHeld(a.Aircraft)
        && !IsParkedOrHeld(b.Aircraft)
        && !HasWingspanLateralClearance(a.Aircraft, dirA, b.Aircraft)
        && !HasWingspanLateralClearance(b.Aircraft, dirB, a.Aircraft);

    /// <summary>
    /// The tie-break winner's side of a mutually yielding pusher pair: it takes no pushback yield for the pusher holding
    /// for it, but — the pair kind being exclusive, so nothing else stops it — it does not push through that pusher either.
    /// It continues when the rest of its push leg clears the holder laterally (<see cref="PushLegClearanceFt"/>), and
    /// otherwise brakes to the stop ring against it as against a stationary aircraft, the same treatment
    /// <see cref="TryResolveGiveWayToPushback"/> gives a pusher a mover holds for.
    /// </summary>
    private static void LetTieBreakWinnerGo(PushbackParty winner, PushbackParty holder, double distFt, Action<string>? diagnosticLog)
    {
        if (winner.Direction is not { } pushDir)
        {
            return;
        }

        if (
            (PushLegClearanceFt(winner.Aircraft, holder.Aircraft) is { } pushPathClearanceFt)
            && (RequiredLateralClearanceFt(winner.Aircraft, holder.Aircraft) is { } requiredLateralFt)
        )
        {
            if (pushPathClearanceFt > requiredLateralFt)
            {
                diagnosticLog?.Invoke(
                    $"    [Pushback] {winner.Aircraft.Callsign} push path clears {holder.Aircraft.Callsign} by {pushPathClearanceFt:F0}ft, continues"
                );
                return;
            }

            diagnosticLog?.Invoke(
                $"    [Pushback] {winner.Aircraft.Callsign} push path passes {holder.Aircraft.Callsign} at "
                    + $"{pushPathClearanceFt:F0}ft < clear({requiredLateralFt:F0}ft)"
            );
        }

        ApplyClosingLimit(winner.Aircraft, pushDir, holder.Aircraft, MovementState.Stationary, distFt, diagnosticLog);
    }

    /// <summary>
    /// Picks which of two mutually yielding pushers (<see cref="AreMutuallyYieldingPushers"/>) keeps going; the other
    /// holds for it. Lexicographic on exact keys (<see cref="PushRank.Order"/>), so the order is total: of any two pushers
    /// exactly one holds, and two pushers never each wait for the other:
    /// <list type="number">
    /// <item>A live-traffic shadow is never the one held: the simulated aircraft is (the rule
    /// <see cref="ChooseMutualStopHolder"/> follows).</item>
    /// <item>When the rest of exactly one side's push leg clears the other laterally (<see cref="PushLegClearanceFt"/> over
    /// <see cref="RequiredLateralClearanceFt"/>), that side goes: it can finish its push past the holder, where the other,
    /// its leg running into the holder, could only brake to the stop ring and hold both.</item>
    /// <item>A committed push off a stand (<see cref="PushbackPhase.HasRampPriority"/>) beats one still on its stand.</item>
    /// <item>A push off a stand beats a tow from a non-stand origin.</item>
    /// <item>The one that has moved further from where its move began goes, compared exactly.</item>
    /// <item>The lower callsign goes (<see cref="ChooseMutualStopHolder"/>'s convention: the higher callsign holds).</item>
    /// </list>
    /// Stateless: recomputed on every pass from the phases' recorded starts, so a snapshot restore reproduces it. The
    /// winner still yields to third aircraft in their own pairs. 7110.65 §3-7-2 NOTE 2 leaves the nonmovement area to
    /// the pilots and the ramp operator, so this models the ramp convention rather than an ATC instruction.
    /// </summary>
    private static (AircraftState Winner, string Reason) ChoosePushbackPairWinner(AircraftState a, AircraftState b)
    {
        var rankA = PushRank.Of(a, b);
        var rankB = PushRank.Of(b, a);
        foreach ((string reason, Func<PushRank, PushRank, int> compare) in PushRank.Order)
        {
            int order = compare(rankA, rankB);
            if (order != 0)
            {
                return (order > 0 ? a : b, reason);
            }
        }

        return (a, "identical keys");
    }

    /// <summary>
    /// The keys <see cref="ChoosePushbackPairWinner"/> orders a pusher by against the other pusher of its pair: its
    /// callsign, whether it is a live-traffic shadow, whether the rest of its push leg clears the other, whether its push
    /// off a stand is committed, whether the tow started on a stand at all, and how far (ft) it has moved from where its
    /// current move began.
    /// </summary>
    private readonly record struct PushRank(string Callsign, bool IsShadow, bool LegClears, bool Committed, bool OffStand, double MovedFt)
    {
        /// <summary>
        /// The keys in the order they decide, each with the reason the log gives; a positive comparison means the first
        /// pusher goes.
        /// </summary>
        public static readonly (string Reason, Func<PushRank, PushRank, int> Compare)[] Order =
        [
            ("shadow", (x, y) => x.IsShadow.CompareTo(y.IsShadow)),
            ("push leg clears", (x, y) => x.LegClears.CompareTo(y.LegClears)),
            ("committed push", (x, y) => x.Committed.CompareTo(y.Committed)),
            ("push off a stand", (x, y) => x.OffStand.CompareTo(y.OffStand)),
            ("moved further", (x, y) => x.MovedFt.CompareTo(y.MovedFt)),
            ("lower callsign", (x, y) => string.CompareOrdinal(y.Callsign, x.Callsign)),
        ];

        public static PushRank Of(AircraftState aircraft, AircraftState other)
        {
            bool legClears =
                (PushLegClearanceFt(aircraft, other) is { } legFt)
                && (RequiredLateralClearanceFt(aircraft, other) is { } requiredFt)
                && (legFt > requiredFt);
            if (aircraft.Phases?.CurrentPhase is not PushbackPhase phase)
            {
                return new PushRank(aircraft.Callsign, aircraft.IsShadow, legClears, Committed: false, OffStand: false, MovedFt: 0.0);
            }

            double movedFt = GeoMath.DistanceNm(phase.StartPose(aircraft).Position, aircraft.Position) * FtPerNm;
            bool offStand = phase.StartsAtStand || phase.ContinuesStandPushOff;
            return new PushRank(aircraft.Callsign, aircraft.IsShadow, legClears, phase.HasRampPriority(aircraft), offStand, movedFt);
        }
    }

    /// <summary>
    /// True when <paramref name="pusher"/> and <paramref name="mover"/> would hold each other up: the mover
    /// lies ahead of the push direction with no lateral room (so the pusher yields for it) while the pusher
    /// lies within 90° of the mover's own direction (so the mover trails it). The mover's side is a trail
    /// limit rather than an outright stop — it pins to zero only inside <see cref="GetSeparation"/>'s stop
    /// distance and otherwise matches the pusher's speed — but against a pusher held at zero, matching its
    /// speed is a stop, so the pair still wedges.
    ///
    /// <para>Three carve-outs, each a case where the mover must not be the one to give way:</para>
    /// <list type="bullet">
    /// <item>An aircraft on the runway surface or in <see cref="CrossingRunwayPhase"/> is never held for a
    /// ramp push — it has to clear the runway it is on, into the ramp if that is where it is going
    /// (AIM 4-3-21.b).</item>
    /// <item>A live-traffic shadow cannot be held at all (nothing of ours drives it), so the pusher keeps its
    /// hard stop — the same rule <see cref="ChooseMutualStopHolder"/> follows.</item>
    /// <item>Only the committed push off a stand has priority (<see cref="PushbackPhase.HasRampPriority"/>):
    /// leg 1 of a push off a stand, once its tail is in the lane. A push still on its stand can wait for the
    /// traffic to pass, and a tow already repositioning — a later leg of a tug move, or the push half of a
    /// three-point turn after a reversal — is ordinary ramp traffic.</item>
    /// </list>
    /// </summary>
    private static bool WouldDeadlock(PushbackParty pusher, PushbackParty mover)
    {
        if ((pusher.State != MovementState.Pushing) || (pusher.Direction is not { } pushDir))
        {
            return false;
        }

        if (
            (mover.Direction is not { } moveDir)
            || (mover.State is MovementState.Pushing or MovementState.Stationary or MovementState.External)
            || IsParkedOrHeld(mover.Aircraft)
        )
        {
            return false;
        }

        if ((RankOf(mover.Aircraft) != RunwayRank.Rest) || (mover.Aircraft.Phases?.CurrentPhase is CrossingRunwayPhase))
        {
            return false;
        }

        if ((pusher.Aircraft.Phases?.CurrentPhase as PushbackPhase)?.HasRampPriority(pusher.Aircraft) != true)
        {
            return false;
        }

        if (HasWingspanLateralClearance(pusher.Aircraft, pushDir, mover.Aircraft))
        {
            return false;
        }

        return HeadingDifference(moveDir, GeoMath.BearingTo(mover.Aircraft.Position, pusher.Aircraft.Position)) < 90;
    }

    /// <summary>
    /// Breaks the pusher-versus-mover wedge (<see cref="WouldDeadlock"/>) in favour of the pushback: the
    /// taxiing aircraft gives way and holds — annotated with <see cref="AircraftGroundOps.AutoYieldTarget"/>
    /// so the operator sees who it is waiting for — and the pusher runs through the graduated closing logic
    /// instead. The holder is handed to that logic as <see cref="MovementState.Stationary"/>, which it is:
    /// that re-opens the wingspan bypass, so the pusher clears an aircraft holding a lane away and stops only
    /// for one it would actually hit. Returns the pusher, whose limit this has settled — none when the push
    /// continues — or null (leaving the pair to the independent sides) when the two would not wedge.
    ///
    /// <para>Priority goes to the push off a stand — leg 1 of it, and nothing else — because of what each
    /// aircraft occupies: a pusher mid-lane blocks it whether it is moving or stopped, so holding it frees
    /// nothing and keeps the lane blocked longer, while the taxiing aircraft can wait where it is. The tug crew
    /// also faces the aircraft and cannot see traffic behind the tail, so it is the worse party to ask for a
    /// judgement. A tow that has already repositioned — a later leg of a tug move, or the push half of a
    /// three-point turn after a reversal — has stopped once and can stop again, so it yields like any other
    /// ramp traffic. 7110.65 §3-7-2 NOTE 2 leaves separation in the nonmovement area to the pilots and the ramp
    /// operator, so this models the ramp convention rather than an ATC instruction.</para>
    ///
    /// <para>The closing logic is skipped outright when the rest of the push clears the holder laterally, and
    /// this is the mirror of <see cref="RouteLateralClearanceFt"/>: a mover's remaining route says where it
    /// will drive, and a pusher's remaining push leg (<see cref="PushbackPhase.TryGetPushLegEnd"/>) says where
    /// its tail will go. Without it a pusher whose tail is already swinging away down the alley still stops
    /// inside its own stop ring against the aircraft holding for it — the instantaneous geometry says "dead
    /// ahead" while the leg says "past and gone" — and neither moves again before the tick budget runs out.
    /// The holder is stationary by then, so the wingspan bypass cannot release the pusher either: at that
    /// range the two are closer than two half-spans however the push is aimed.</para>
    /// </summary>
    private static AircraftState? TryResolveGiveWayToPushback(PushbackParty pusher, PushbackParty mover, double distFt, Action<string>? diagnosticLog)
    {
        if ((pusher.Direction is not { } pushDir) || !WouldDeadlock(pusher, mover))
        {
            return null;
        }

        diagnosticLog?.Invoke($"    [Pushback] {mover.Aircraft.Callsign} gives way to pushback {pusher.Aircraft.Callsign}: dist={distFt:F0}ft");
        ApplyMinLimit(mover.Aircraft, 0, new LimitCause("give way to pushback", pusher.Aircraft, distFt, RoomFt: null, StopClosure.None));
        mover.Aircraft.Ground.AutoYieldTarget = pusher.Aircraft.Callsign;
        mover.Aircraft.Ground.AutoYieldIsFollowing = false;

        if (
            (PushLegClearanceFt(pusher.Aircraft, mover.Aircraft) is { } pushPathClearanceFt)
            && (RequiredLateralClearanceFt(pusher.Aircraft, mover.Aircraft) is { } requiredLateralFt)
        )
        {
            if (pushPathClearanceFt > requiredLateralFt)
            {
                diagnosticLog?.Invoke(
                    $"    [Pushback] {pusher.Aircraft.Callsign} push path clears {mover.Aircraft.Callsign} by {pushPathClearanceFt:F0}ft, continues"
                );
                return pusher.Aircraft;
            }

            diagnosticLog?.Invoke(
                $"    [Pushback] {pusher.Aircraft.Callsign} push path passes {mover.Aircraft.Callsign} at "
                    + $"{pushPathClearanceFt:F0}ft < clear({requiredLateralFt:F0}ft)"
            );
        }

        ApplyClosingLimit(pusher.Aircraft, pushDir, mover.Aircraft, MovementState.Stationary, distFt, diagnosticLog);
        return pusher.Aircraft;
    }

    /// <summary>
    /// How close the rest of <paramref name="pusher"/>'s push leg comes to <paramref name="mover"/>, in feet,
    /// or null when the pusher is not in a pushback whose leg end is known. The track measured is the straight
    /// segment from where the pusher is now to where the leg ends
    /// (<see cref="PushbackPhase.TryGetPushLegEnd"/>); a targeted push arcs onto that point rather than sliding
    /// along the chord, and the chord is the pessimistic side of that arc (it cuts the corner the tail swings
    /// around).
    /// </summary>
    private static double? PushLegClearanceFt(AircraftState pusher, AircraftState mover)
    {
        if (pusher.Phases?.CurrentPhase is not PushbackPhase pushbackPhase)
        {
            return null;
        }

        if (!pushbackPhase.TryGetPushLegEnd(pusher, out LatLon legEnd))
        {
            return null;
        }

        return GeoMath.DistanceToSegmentFt(mover.Position, pusher.Position, legEnd);
    }

    /// <summary>
    /// One aircraft's obligation within a pushback pair: a pusher yields to
    /// <paramref name="other"/> (<see cref="PushbackYieldForTraffic"/>), any other mover trails it,
    /// and a stationary aircraft owes nothing.
    /// </summary>
    private static void ResolvePushbackSide(PushbackParty subject, PushbackParty other, double distFt, Action<string>? diagnosticLog)
    {
        if (subject.Direction is not { } dir)
        {
            return;
        }

        if (subject.State == MovementState.Pushing)
        {
            PushbackYieldForTraffic(subject.Aircraft, dir, other, distFt, diagnosticLog);
            return;
        }

        if (subject.State == MovementState.Stationary)
        {
            return;
        }

        if (HeadingDifference(dir, GeoMath.BearingTo(subject.Aircraft.Position, other.Aircraft.Position)) < 90)
        {
            ApplyTrailLimit(subject.Aircraft, other.Aircraft, distFt);
        }
    }

    /// <summary>
    /// The yield a pushing aircraft owes another aircraft inside the pushback buffer.
    ///
    /// <para>A genuinely parked/held neighbor at a gate is a passable obstacle, not a hard stop — a
    /// gate pushback clears an aircraft parked at the adjacent gate as a matter of course. The pusher carries on
    /// when the rest of its move keeps its outline clear of the neighbour's (<see cref="TugMoveFoulsParkedAt"/>);
    /// otherwise it is braked at the towbar rate to a stop where the two outlines would meet
    /// (<see cref="TugMoveLimit"/>), showing the neighbour as what it waits for, and is not slowed at all further out
    /// than that rather than being held off by the nose-to-nose distances. That
    /// still leaves one wedge the controller has to break by hand: a move that runs into a parked aircraft stops short
    /// of it, and neither moves again without a new clearance or a BREAK.</para>
    ///
    /// <para>Against a mover, the pusher stops only for traffic ahead of its push direction that it
    /// cannot clear laterally: two tugs in adjacent alley lanes pass wingtip-to-wingtip and must not
    /// freeze each other (SFO ATCT SOP 3-5.c.i uses spots 5A/5B — and 6A/6B — simultaneously for
    /// aircraft smaller than a B757). A pusher aimed at the other's fuselage still stops. The lateral
    /// room is measured along <paramref name="pushDir"/>, not the nose: a pusher moves tail-first, so
    /// its heading points the opposite way.</para>
    ///
    /// <para>The lateral test is re-evaluated on every detector pass rather than latched, so a mover
    /// converging into the push corridor re-pins the pusher the instant the clearance is lost. The
    /// Pushback pair kind is exclusive — no closing or head-on check runs on top of it — so this is
    /// the pair's only guard, which is why it has to be instantaneous rather than one-shot.</para>
    ///
    /// <para>The yield does not run when it would hold both aircraft: a mover that owes the pusher a trail
    /// limit gives way instead (<see cref="TryResolveGiveWayToPushback"/>), because a push already out in the
    /// lane has priority. This path therefore stops a pusher only for an aircraft that is itself free to keep
    /// moving, or for one of that method's carve-outs (a runway crosser, a live-traffic shadow, a push still
    /// on its stand). A pusher pinned here carries <see cref="AircraftGroundOps.AutoYieldTarget"/> so the
    /// operator can see what a stalled PUSH is waiting for.</para>
    ///
    /// <para>The <see cref="WingspanLateralCheckEnabled"/> / <see cref="WingspanLateralCheckRequireStationary"/>
    /// toggles that gate the same geometry inside <see cref="ComputeClosingLimit"/> deliberately do not
    /// gate this path. The pushback yield exists only against movers, so a stationary-only gate would
    /// disable it outright; the toggles are A/B capture instrumentation for the taxi closing check
    /// (<c>Skw3078FixComparisonCapture</c>), not a switch for pushback geometry.</para>
    /// </summary>
    private static void PushbackYieldForTraffic(
        AircraftState pusher,
        double pushDir,
        PushbackParty other,
        double distFt,
        Action<string>? diagnosticLog
    )
    {
        if (IsParkedOrHeld(other.Aircraft))
        {
            if (ComputeClosingLimit(pusher, pushDir, other.Aircraft, other.State, distFt, diagnosticLog) is { } closing)
            {
                ApplyMinLimit(pusher, closing.Limit, new LimitCause(closing.Reason, other.Aircraft, distFt, closing.RoomFt, closing.Closure));
                ShowTugMoveYield(pusher, other.Aircraft, closing.Limit);
            }

            return;
        }

        if (!HasWingspanLateralClearance(pusher, pushDir, other.Aircraft))
        {
            ApplyMinLimit(pusher, 0, new LimitCause("pushback yield", other.Aircraft, distFt, RoomFt: null, StopClosure.None));
            pusher.Ground.AutoYieldTarget = other.Aircraft.Callsign;
            pusher.Ground.AutoYieldIsFollowing = false;
        }
    }

    /// <summary>
    /// Runs <see cref="ApplyClosingLimit"/> in both directions and (optionally)
    /// <see cref="ResolveHeadOn"/>. Used by the Crossing kind and as the safety
    /// net for Converging.
    /// </summary>
    private static void ApplyCrossingChecks(
        AircraftState a,
        MovementState stateA,
        double? dirA,
        AircraftState b,
        MovementState stateB,
        double? dirB,
        double distFt,
        bool skipHeadOn,
        Action<string>? diagnosticLog
    )
    {
        if ((dirA is { } moveDirA) && (ComputeClosingLimit(a, moveDirA, b, stateB, distFt, diagnosticLog) is { } limitA))
        {
            ApplyHolderLimit(a, limitA, b, distFt);
        }
        if ((dirB is { } moveDirB) && (ComputeClosingLimit(b, moveDirB, a, stateA, distFt, diagnosticLog) is { } limitB))
        {
            ApplyHolderLimit(b, limitB, a, distFt);
        }
        if (!skipHeadOn && dirA is not null && dirB is not null && a.GroundSpeed > 0 && b.GroundSpeed > 0)
        {
            ResolveHeadOn(a, dirA.Value, b, dirB.Value, distFt, arbitrate: true);
        }
    }

    /// <summary>
    /// Computes the closing-proximity speed limit <paramref name="mover"/> should
    /// receive for <paramref name="obstacle"/>, or null when no limit applies (not
    /// closing, can pass laterally, or the on-runway exemption). Pure — does not
    /// mutate state — so a caller can arbitrate between the two directions before
    /// committing a limit (see <see cref="ResolveCrossing"/>).
    ///
    /// <para>There are two lateral tests, and either one passing clears the obstacle. The first measures
    /// the room along the mover's current direction. The second — only against a genuinely parked or held
    /// obstacle, which will still be there when the mover arrives — measures it along the mover's remaining
    /// route (<see cref="RouteLateralClearanceFt"/>). A mover that has braked mid-turn points somewhere
    /// between the two lanes, so its nose says nothing about the lane it is going to follow; the route does,
    /// and the route is what it will actually drive. Without this, an aircraft stopped inside a parked
    /// neighbour's stop ring can never leave: the geometry that would release it only appears once it moves
    /// (SFO's ramp alley lanes are ~140 ft apart and a B738 beside an E75L needs ~131 ft, so the pass is
    /// legitimate, but the nose-based test never sees it).</para>
    ///
    /// <para>A tug-moved mover (<see cref="PushbackPhase"/>, push or pull) against a parked or held obstacle takes
    /// neither lateral test, and none of the nose-based distances below: <see cref="TugMoveLimit"/> decides that pair on
    /// its own, by sweeping both outlines along the rest of the move and stopping the mover where the outlines would
    /// meet rather than where the two noses would.</para>
    /// </summary>
    private static (double Limit, string Reason, double? RoomFt, StopClosure Closure)? ComputeClosingLimit(
        AircraftState mover,
        double moveDir,
        AircraftState obstacle,
        MovementState obstacleState,
        double distFt,
        Action<string>? diagnosticLog
    )
    {
        var pair = new ClosingPair(mover, moveDir, obstacle, obstacleState, distFt);
        if (!IsClosing(pair, diagnosticLog))
        {
            return null;
        }

        if (TugMoveAgainstParked(mover, obstacle) is { } tugMove)
        {
            return TugMoveLimit(mover, tugMove, obstacle, distFt, diagnosticLog);
        }

        return ClosingSeparation(pair, diagnosticLog) is { } separation ? TrailBandLimit(pair, separation, diagnosticLog) : null;
    }

    /// <summary>
    /// The pair the closing check judges: the mover, the direction it moves in, the obstacle, how the obstacle moves, and how far
    /// apart the two are (ft).
    /// </summary>
    private readonly record struct ClosingPair(
        AircraftState Mover,
        double MoveDir,
        AircraftState Obstacle,
        MovementState ObstacleState,
        double DistFt
    );

    /// <summary>
    /// Whether the mover is in conflict with the obstacle it closes on: the closing check's own test (<see cref="ComputeClosingLimit"/>)
    /// puts the pair inside its trail band, or a tug move's sweep stops it — whether or not a limit comes of it. A crosser takes no
    /// limit until its braking onto the stop ring binds (<see cref="CrossingStopCurveKts"/>), which may be inside the band or not
    /// yet, yet the two are in conflict across the whole band.
    /// </summary>
    private static bool IsClosingConflict(ClosingPair pair)
    {
        if (!IsClosing(pair, diagnosticLog: null))
        {
            return false;
        }

        if (TugMoveAgainstParked(pair.Mover, pair.Obstacle) is { } tugMove)
        {
            return TugMoveLimit(pair.Mover, tugMove, pair.Obstacle, pair.DistFt, diagnosticLog: null) is not null;
        }

        return (ClosingSeparation(pair, diagnosticLog: null) is { } separation) && (pair.DistFt <= separation.TrailFt);
    }

    /// <summary>Whether the mover is moving toward the obstacle: the obstacle lies less than 90° off its direction of travel.</summary>
    private static bool IsClosing(ClosingPair pair, Action<string>? diagnosticLog)
    {
        double angleDiff = ClosingAngleDeg(pair);
        if (angleDiff < 90)
        {
            return true;
        }

        diagnosticLog?.Invoke($"    [Closing] {pair.Mover.Callsign}→{pair.Obstacle.Callsign}: diff={angleDiff:F0}° ≥90, not closing");
        return false;
    }

    /// <summary>How far (deg) the bearing to the obstacle lies off the mover's direction of travel.</summary>
    private static double ClosingAngleDeg(ClosingPair pair) =>
        HeadingDifference(pair.MoveDir, GeoMath.BearingTo(pair.Mover.Position, pair.Obstacle.Position));

    /// <summary>
    /// The pair's stop ring and trail band (<see cref="GetSeparation"/>) for a mover closing on the obstacle that cannot pass it
    /// laterally (<see cref="CanPassLaterally"/>) and is not on the runway closing on an off-runway obstacle at rest; null
    /// otherwise.
    /// </summary>
    private static (double StopFt, double TrailFt)? ClosingSeparation(ClosingPair pair, Action<string>? diagnosticLog)
    {
        if (CanPassLaterally(pair, diagnosticLog))
        {
            return null;
        }

        // Only a runway user's path is the runway itself: an obstacle at rest off it is not in that path. A vacating aircraft's
        // path leads off the runway, so it keeps its closing check; and an obstacle on the runway by its phase stays one.
        if (
            (RankOf(pair.Mover) == RunwayRank.RunwayUser)
            && (RankOf(pair.Obstacle) != RunwayRank.RunwayUser)
            && !IsOnRunwayByPhase(pair.Obstacle)
            && (pair.Obstacle.GroundSpeed <= 0)
        )
        {
            diagnosticLog?.Invoke($"    [Closing] {pair.Mover.Callsign}→{pair.Obstacle.Callsign}: mover on runway, obstacle off-runway, skip");
            return null;
        }

        return GetSeparation(pair.Obstacle, pair.Mover);
    }

    /// <summary>
    /// Whether the mover passes the obstacle with its wingspan clear: along its current direction, or — against an obstacle that
    /// stays put — along its remaining route (<see cref="PassesAlongRoute"/>). Judged only where the lateral test applies
    /// (<see cref="LateralTestApplies"/>).
    /// </summary>
    private static bool CanPassLaterally(ClosingPair pair, Action<string>? diagnosticLog)
    {
        if (
            !WingspanLateralCheckEnabled
            || !LateralTestApplies(pair)
            || (RequiredLateralClearanceFt(pair.Mover, pair.Obstacle) is not { } requiredLateralFt)
        )
        {
            return false;
        }

        double lateralFt = pair.DistFt * Math.Sin(ClosingAngleDeg(pair) * Math.PI / 180.0);
        if (lateralFt > requiredLateralFt)
        {
            diagnosticLog?.Invoke(
                $"    [Closing] {pair.Mover.Callsign}→{pair.Obstacle.Callsign}: lateral={lateralFt:F0}ft > clearance({requiredLateralFt:F0}ft), can pass"
            );
            return true;
        }

        return PassesAlongRoute(pair, requiredLateralFt, diagnosticLog);
    }

    /// <summary>
    /// Whether the lateral test applies to the pair: against a stationary obstacle (or any, when the test does not require one),
    /// and against a moving obstacle on a neighbouring, near-parallel lane, which is passing rather than closing — as safe to pass
    /// as a parked one, and without this the straight-line distance rule crawls or stops an aircraft for traffic on the taxiway
    /// alongside it.
    /// </summary>
    private static bool LateralTestApplies(ClosingPair pair)
    {
        bool isStationary = pair.ObstacleState == MovementState.Stationary;
        if (!WingspanLateralCheckRequireStationary || isStationary)
        {
            return true;
        }

        double obstacleDir = pair.Obstacle.Ground.PushbackTrueHeading?.Degrees ?? pair.Obstacle.TrueHeading.Degrees;
        return HasParallelTrackLateralRoom(pair.Mover, pair.MoveDir, pair.Obstacle, obstacleDir);
    }

    /// <summary>
    /// Whether the mover's remaining route (<see cref="RouteLateralClearanceFt"/>) passes an obstacle that stays put — parked or
    /// held, or a live-traffic shadow standing still, as fixed as a parked aircraft since nothing we do moves it — at least
    /// <paramref name="requiredLateralFt"/> off.
    /// </summary>
    private static bool PassesAlongRoute(ClosingPair pair, double requiredLateralFt, Action<string>? diagnosticLog)
    {
        bool staysPut = IsParkedOrHeld(pair.Obstacle) || (pair.Obstacle.IsShadow && (pair.ObstacleState == MovementState.Stationary));
        if (
            !staysPut
            || (RouteLateralClearanceFt(pair.Mover, pair.Obstacle, pair.DistFt) is not { } routeLateralFt)
            || (routeLateralFt <= requiredLateralFt)
        )
        {
            return false;
        }

        diagnosticLog?.Invoke(
            $"    [Closing] {pair.Mover.Callsign}→{pair.Obstacle.Callsign}: route lateral={routeLateralFt:F0}ft "
                + $"≥ clear({requiredLateralFt:F0}ft), can pass"
        );
        return true;
    }

    /// <summary>
    /// The closing limit for the pair's <paramref name="separation"/>: a stop inside the stop ring. Outside it, a crosser is
    /// decided by its braking onto the ring alone (<see cref="CrossingStopCurveKts"/>, none until the ring is inside its own
    /// stopping distance), whether or not the pair is inside the trail band: the band (the ring plus 100 ft) can be shorter than a
    /// crosser's taxi-rate stopping distance, and waiting for it would leave only the firm rate. Any other mover takes the
    /// proximity trail (the obstacle's speed, at least <see cref="SlowTaxiSpeedKts"/>) inside the trail band and nothing beyond
    /// it. Both carry the room left before the ring.
    /// </summary>
    private static (double Limit, string Reason, double? RoomFt, StopClosure Closure)? TrailBandLimit(
        ClosingPair pair,
        (double StopFt, double TrailFt) separation,
        Action<string>? diagnosticLog
    )
    {
        string route = $"{pair.Mover.Callsign}→{pair.Obstacle.Callsign}";
        if (pair.DistFt <= separation.StopFt)
        {
            diagnosticLog?.Invoke($"    [Closing] {route}: {pair.DistFt:F0}ft ≤ stop({separation.StopFt:F0}ft) → limit=0");
            return (0, "proximity stop", null, StopClosure.None);
        }

        double roomFt = pair.DistFt - separation.StopFt;
        if (HasCrossingStopCurve(pair.Mover))
        {
            StopClosure closure = ClosureOf(pair.Obstacle, pair.Mover);
            double? curveKts = CrossingStopCurveKts(pair.Mover, closure, roomFt);
            diagnosticLog?.Invoke(
                $"    [Closing] {route}: {pair.DistFt:F0}ft, {roomFt:F0}ft to the stop ring, crossing a runway, "
                    + $"stop curve={curveKts?.ToString("F1") ?? "none"}"
            );
            return curveKts is { } crossingKts ? (crossingKts, CrossingStopCurveReason, roomFt, closure) : null;
        }

        if (pair.DistFt > separation.TrailFt)
        {
            return null;
        }

        double limitSpeed = Math.Max(pair.Obstacle.GroundSpeed, SlowTaxiSpeedKts);
        diagnosticLog?.Invoke($"    [Closing] {route}: {pair.DistFt:F0}ft ≤ trail({separation.TrailFt:F0}ft) → limit={limitSpeed:F1}");
        return (limitSpeed, "proximity trail", roomFt, StopClosure.None);
    }

    /// <summary>
    /// The closest <paramref name="mover"/>'s remaining taxi route passes <paramref name="obstacle"/>, in
    /// feet, or null when it has no route left to drive. Each segment is measured against the pavement it
    /// actually follows (<see cref="EdgeClearanceFt"/>), not the chord between its nodes.
    ///
    /// <para>The walk is charged only what is left to drive — the remainder of the current segment, then
    /// whole segments — and stops at <c>1.5 × <paramref name="distFt"/></c> plus the obstacle's length. A
    /// closing limit only ever applies inside the trail distance (~356 ft for the largest pair), so a pass
    /// point further along the route than that cannot matter this tick, and the 1.5 factor covers the route
    /// reaching the obstacle around an L rather than straight at it. The uncovered case is a route that
    /// doubles back past the same obstacle a second time, beyond the bound: the first pass governs, and the
    /// second is re-measured on its own approach.</para>
    ///
    /// <para>The segment measured is always the whole current segment, including the part already behind the
    /// mover — that costs nothing and can only lower the answer, which is the safe direction.</para>
    /// </summary>
    private static double? RouteLateralClearanceFt(AircraftState mover, AircraftState obstacle, double distFt)
    {
        // A crossing aircraft's route stays on the crossing's entry segment until the crossing ends (docs/ground/navigator.md), so
        // its path ahead is read from the crossing phase instead: measured from the stale entry segment, a held aircraft just past
        // the far bar reads hundreds of feet off the path and the crosser drives into it.
        double boundFt = (distFt * RouteClearanceBoundFactor) + AircraftLength.ResolveFt(obstacle.AircraftType);
        var walk = new RouteClearanceWalk(mover.Position, obstacle.Position, boundFt);
        if ((mover.Phases?.CurrentPhase is CrossingRunwayPhase crossing) && (crossing.PathAhead(mover) is { } pathAhead))
        {
            if (WalkSegments(ref walk, pathAhead.Slice, pathAhead.SliceStart) && (pathAhead.Onward is { } onward))
            {
                WalkSegments(ref walk, onward, pathAhead.OnwardStart);
            }
        }
        else if (FollowingPhase.DrivenRouteOf(mover) is { } route)
        {
            WalkSegments(ref walk, route, Math.Max(route.CurrentSegmentIndex, 0));
        }

        return walk.ClosestFt;
    }

    /// <summary>
    /// Takes <paramref name="route"/>'s segments from <paramref name="start"/> on into <paramref name="walk"/>; false once the
    /// walk has walked its bound.
    /// </summary>
    private static bool WalkSegments(ref RouteClearanceWalk walk, TaxiRoute route, int start)
    {
        for (int i = start; i < route.Segments.Count; i++)
        {
            if (!walk.Add(route.Segments[i].Edge))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The walk <see cref="RouteLateralClearanceFt"/> takes along a mover's path ahead: the closest any edge passes the
    /// obstacle, each edge counted until <paramref name="boundFt"/> of path has been walked, the first from the mover's position.
    /// </summary>
    private struct RouteClearanceWalk(LatLon moverPosition, LatLon obstaclePosition, double boundFt)
    {
        private double _walkedFt;
        private bool _started;

        /// <summary>The closest (ft) an edge walked so far passes the obstacle; null before the first.</summary>
        public double? ClosestFt { get; private set; }

        /// <summary>Takes <paramref name="edge"/> into the walk; false, taking nothing, once the bound has been walked.</summary>
        public bool Add(DirectionalEdge edge)
        {
            if (_walkedFt >= boundFt)
            {
                return false;
            }

            double segmentFt = EdgeClearanceFt(edge, obstaclePosition);
            ClosestFt = ClosestFt is { } best ? Math.Min(best, segmentFt) : segmentFt;
            _walkedFt += _started ? edge.DistanceNm * FtPerNm : GeoMath.DistanceNm(moverPosition, edge.ToNode.Position) * FtPerNm;
            _started = true;
            return true;
        }
    }

    /// <summary>
    /// Closest approach of one route edge to <paramref name="point"/>, in feet, following the pavement: a
    /// fillet arc is sampled off its Bézier and a straight edge is walked through its
    /// <see cref="GroundEdge.IntermediatePoints"/>. The chord would be optimistic by the segment's sagitta —
    /// 22 ft on a 75 ft / 90° gate fillet and 46 ft on a 600 ft / 45° high-speed turnoff, both past the 25 ft
    /// <see cref="GroundOutlineSweep.WingtipBufferFt"/> the caller adds, so a mover could be cleared to pass a wingtip it would
    /// actually swing into.
    /// </summary>
    private static double EdgeClearanceFt(DirectionalEdge edge, LatLon point)
    {
        if (edge.Edge is GroundArc arc)
        {
            CubicBezier curve = arc.ToBezier();
            (double Lat, double Lon) previous = curve.Evaluate(0.0);
            double best = double.MaxValue;
            for (int i = 1; i <= ArcClearanceSamples; i++)
            {
                (double Lat, double Lon) next = curve.Evaluate((double)i / ArcClearanceSamples);
                best = Math.Min(best, GeoMath.DistanceToSegmentFt(point.Lat, point.Lon, previous.Lat, previous.Lon, next.Lat, next.Lon));
                previous = next;
            }

            return best;
        }

        if (edge.Edge is not GroundEdge { IntermediatePoints.Count: > 0 } straight)
        {
            return GeoMath.DistanceToSegmentFt(point, edge.FromNode.Position, edge.ToNode.Position);
        }

        // Walked in the edge's own node order, not the traversal's: a closest approach does not care which
        // way the aircraft drives it, and the intermediate points are stored against Nodes[0] → Nodes[1].
        LatLon from = straight.Nodes[0].Position;
        LatLon to = straight.Nodes[1].Position;
        double closest = double.MaxValue;
        (double Lat, double Lon) previousPoint = (from.Lat, from.Lon);
        foreach ((double lat, double lon) in straight.IntermediatePoints)
        {
            closest = Math.Min(closest, GeoMath.DistanceToSegmentFt(point.Lat, point.Lon, previousPoint.Lat, previousPoint.Lon, lat, lon));
            previousPoint = (lat, lon);
        }

        return Math.Min(closest, GeoMath.DistanceToSegmentFt(point.Lat, point.Lon, previousPoint.Lat, previousPoint.Lon, to.Lat, to.Lon));
    }

    /// <summary>
    /// The tug move <paramref name="mover"/> is flying, when <paramref name="obstacle"/> is a parked or held aircraft
    /// the outline rule (<see cref="TugMoveFoulsParkedAt"/>) judges it against; null otherwise.
    /// </summary>
    private static PushbackPhase? TugMoveAgainstParked(AircraftState mover, AircraftState obstacle) =>
        (mover.Phases?.CurrentPhase is PushbackPhase tugMove) && IsParkedOrHeld(obstacle) ? tugMove : null;

    /// <summary>
    /// An aircraft a forced tow does not stop for: at a stand (<see cref="AtParkingPhase"/>) or resting after a push
    /// (<see cref="HoldingAfterPushbackPhase"/>). The parked cases of <see cref="IsParkedOrHeld(AircraftState)"/> without the
    /// held ones: an aircraft stopped on the pavement under a controller hold, or holding in position, is not parked.
    /// </summary>
    private static bool IsParkedAtStandOrAfterPush(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase is AtParkingPhase or HoldingAfterPushbackPhase;

    /// <summary>
    /// The rest of the run the tug goes straight on into after the move it is flying: the moves of the
    /// <see cref="PushbackPhase"/>s queued behind the current one, up to but not including the first that dwells.
    ///
    /// <para>A dwell is a full stop — the plan reverses there (AC 00-65A §11.17) — so nothing past it needs braking for
    /// yet, and every move of the run up to it is the kind the current one is, which is what lets the sweep judge them
    /// all on the one <see cref="GroundOutlineSize"/>. The run also ends at the terminus phase, which is not a tug
    /// move.</para>
    ///
    /// <para>Sweeping the run rather than the move is what gives the tug room to brake: a neighbour the next move walks
    /// into is invisible while <see cref="PushbackPhase.RemainingPath"/> covers this move alone, and by the time that
    /// move starts the tow is already carrying its commanded speed into it.</para>
    ///
    /// <para>Nothing queued behind the current phase is ever complete — <c>PhaseList</c> marks a phase Completed as it
    /// leaves it and only then advances, so everything past <c>CurrentIndex</c> is still pending — and skipping such an
    /// entry would step the walk over the dwell that ends the run rather than stopping at it.</para>
    ///
    /// <para>Built once per mover per <see cref="ApplySpeedLimits"/> pass (<see cref="_tugRunContinuations"/>): the run
    /// is a property of the mover alone and cannot change inside a pass, and the outline sweep asks for it on every
    /// obstacle the mover is measured against.</para>
    /// </summary>
    private static IReadOnlyList<TugMove> TugRunContinuation(AircraftState mover)
    {
        if (_tugRunContinuations is not { } cache)
        {
            return BuildTugRunContinuation(mover);
        }

        if (!cache.TryGetValue(mover, out IReadOnlyList<TugMove>? cached))
        {
            cached = BuildTugRunContinuation(mover);
            cache[mover] = cached;
        }

        return cached;
    }

    /// <summary>Walks the phases queued behind the mover's current one, as <see cref="TugRunContinuation"/> describes.</summary>
    private static IReadOnlyList<TugMove> BuildTugRunContinuation(AircraftState mover)
    {
        if (mover.Phases is not { } phases)
        {
            return [];
        }

        var moves = new List<TugMove>();
        for (int i = phases.CurrentIndex + 1; i < phases.Phases.Count; i++)
        {
            if (phases.Phases[i] is not PushbackPhase queued)
            {
                break;
            }

            if (queued.Move.DwellBefore)
            {
                break;
            }

            moves.Add(queued.Move);
        }

        return moves;
    }

    /// <summary>
    /// The whole of what a tug move owes a parked or held neighbour: null while it may carry on, otherwise the speed the
    /// tug can still brake to a stop from — at the towbar rate (<see cref="CategoryPerformance.TugDecelRate"/>) — before
    /// the two outlines meet, once the outline sweep (<see cref="TugMoveFoulsParkedAt"/>) says the move fouls the
    /// neighbour within <see cref="TugMoveStopMarginFt"/> of where it is.
    ///
    /// <para>The limit is a braking curve, not a hard stop: at the margin it equals the move's commanded speed
    /// (<see cref="CategoryPerformance.PushbackSpeed"/>), so a move coming into range takes no step down, and it falls
    /// to zero within one detector interval (<see cref="DetectorIntervalSeconds"/>) of the fouled sample. Re-measured
    /// every pass as the mover closes, it walks the tug down at the towbar rate — the hard clamp physics applies to
    /// <see cref="AircraftGroundOps.SpeedLimit"/> follows the curve instead of halting the tow from 5 kt in one
    /// sub-tick, which a towbar cannot do.</para>
    ///
    /// <para>The stop is where the two <em>outlines</em> would meet, not where the two noses would. The nose-based stop
    /// distance (<see cref="GetSeparation"/>) leaves <see cref="StopBufferFt"/> between fuselage ends, which is inside
    /// the <see cref="GroundOutline.TugLeadFt"/> a pull carries ahead of its nose — a tow stopped by it has the tug
    /// already through the parked aircraft — and says nothing at all about where a push's tail or a turn's wingtip
    /// arrives. A move that is still further out than the margin takes no limit at all: it keeps its speed and the sweep
    /// re-measures on the next pass, which is what lets a push creep past a neighbour it will clear.</para>
    /// </summary>
    private static (double Limit, string Reason, double? RoomFt, StopClosure Closure)? TugMoveLimit(
        AircraftState mover,
        PushbackPhase tugMove,
        AircraftState obstacle,
        double distFt,
        Action<string>? diagnosticLog
    )
    {
        // A forced tow (PUSHF / PUSHMF) does not stop for an aircraft at a stand or resting after a push; one held or
        // holding on the pavement still stops it here, and moving traffic still limits it elsewhere.
        if (mover.Ground.ForcedTowIgnoresParked && IsParkedAtStandOrAfterPush(obstacle))
        {
            diagnosticLog?.Invoke($"    [Closing] {mover.Callsign}→{obstacle.Callsign}: forced tow, parked aircraft ignored");
            return null;
        }

        if (distFt > GetSeparation(obstacle, mover).TrailFt)
        {
            diagnosticLog?.Invoke($"    [Closing] {mover.Callsign}→{obstacle.Callsign}: tug move, {distFt:F0}ft beyond trail, no limit");
            return null;
        }

        if (TugMoveFoulsParkedAt(mover, tugMove, obstacle, diagnosticLog) is not { } failAlongFt)
        {
            return null;
        }

        double marginFt = TugMoveStopMarginFt(mover);
        if (failAlongFt > marginFt)
        {
            diagnosticLog?.Invoke(
                $"    [Closing] {mover.Callsign}→{obstacle.Callsign}: tug move fouls {failAlongFt:F1}ft along > margin({marginFt:F1}ft), no limit"
            );
            return null;
        }

        double curveKts = TugMoveBrakingLimitKts(mover, failAlongFt);
        double limitKts = failAlongFt > 0.0 ? Math.Max(curveKts, TowbarBrakingFloorKts(mover)) : curveKts;
        diagnosticLog?.Invoke(
            $"    [Closing] {mover.Callsign}→{obstacle.Callsign}: tug move fouls {failAlongFt:F1}ft along ≤ margin({marginFt:F1}ft) → "
                + $"curve={curveKts:F2}kt, limit={limitKts:F2}kt"
        );
        return (limitKts, "outline stop", null, StopClosure.None);
    }

    /// <summary>
    /// The lowest speed a tow may be limited to on this detector pass, knots: its speed now less what the tug sheds at
    /// the towbar rate (<see cref="CategoryPerformance.TugDecelRate"/>) over one detector interval
    /// (<see cref="DetectorIntervalSeconds"/>). Physics clamps the speed to <see cref="AircraftGroundOps.SpeedLimit"/> at
    /// once, so a limit under this would brake the tow harder than a towbar can.
    ///
    /// <para>The braking curve (<see cref="TugMoveBrakingLimitKts"/>) already falls at the towbar rate while the
    /// fouled point holds still, but that point is the floor crossing interpolated between two path samples, and it
    /// jitters by a foot or two as the samples move with the mover — enough, from a curve the tug is riding, to ask
    /// for more than the towbar rate in one pass. Floored here, the tug sheds the jitter over the next passes instead.
    /// A live pose already under the floor (the fouled point at zero along) is the runtime contact backstop and is
    /// held at zero, not floored.</para>
    ///
    /// <para>The speed is <see cref="AircraftState.GroundSpeed"/>, the one the rest of the tug-move code reads. The floor
    /// relies on the braking curve having already slowed the tug before the fouled point: it only stops a jitter from
    /// asking for more than the towbar rate, so a tow that arrived at the margin still at speed would be floored above
    /// the stop the curve asks for and run on into the neighbour until the contact backstop holds it.</para>
    /// </summary>
    private static double TowbarBrakingFloorKts(AircraftState mover)
    {
        AircraftCategory category = AircraftCategorization.Categorize(mover.AircraftType);
        return Math.Max(0.0, mover.GroundSpeed - (CategoryPerformance.TugDecelRate(category) * DetectorIntervalSeconds));
    }

    /// <summary>
    /// How fast a tug move may be going with <paramref name="failAlongFt"/> feet left to the sample where its outline
    /// fouls the neighbour, knots: the speed it can still brake to a stop from at the towbar rate
    /// (<see cref="CategoryPerformance.TugDecelRate"/>) over what is left after the distance it covers before the next
    /// detector pass (<see cref="DetectorIntervalSeconds"/>) — zero inside that last interval.
    ///
    /// <para>Capped at the move's commanded speed (<see cref="CategoryPerformance.PushbackSpeed"/>), which the curve
    /// reaches exactly at <see cref="TugMoveStopMarginFt"/>: a move that comes into range takes no step down, and the
    /// hard clamp on <see cref="AircraftGroundOps.SpeedLimit"/> then walks it down at the towbar rate as the sweep
    /// re-measures the distance left on every pass.</para>
    /// </summary>
    private static double TugMoveBrakingLimitKts(AircraftState mover, double failAlongFt)
    {
        AircraftCategory category = AircraftCategorization.Categorize(mover.AircraftType);
        double speedKts = CategoryPerformance.PushbackSpeed(category);
        double ftPerSecPerKt = FtPerNm / 3600.0;
        double brakingFt = failAlongFt - (speedKts * DetectorIntervalSeconds * ftPerSecPerKt);
        if (brakingFt <= 0.0)
        {
            return 0.0;
        }

        return Math.Min(speedKts, Math.Sqrt(2.0 * CategoryPerformance.TugDecelRate(category) * brakingFt / ftPerSecPerKt));
    }

    /// <summary>
    /// How far short of the point its outline fouls a neighbour a tug move has to start braking, feet: the distance the
    /// tug needs to brake to a stop at the towbar rate (<see cref="CategoryPerformance.TugDecelRate"/>) from the speed
    /// it would otherwise be moving at, plus the distance it covers at that speed between this detector pass and the
    /// next (<see cref="DetectorIntervalSeconds"/>, the last moment the sweep can still act on it).
    ///
    /// <para>The speed is the move's commanded speed (<see cref="CategoryPerformance.PushbackSpeed"/>), not the live
    /// one, which makes the margin a constant of the pair for the whole move. Measured at the live speed it is zero at
    /// rest: a mover stopped by this rule is released on the very next pass, rolls again, and creeps into the neighbour
    /// a foot at a time. A creep move drops to <see cref="CategoryPerformance.PushbackAlignSpeed"/> only over its last
    /// stretch, and the larger figure is the safe one to brake for.</para>
    ///
    /// <para>The rate is the towbar's, not the aircraft's own brakes
    /// (<see cref="CategoryPerformance.TaxiDecelRate"/>): a tug stops the tow it is connected to, and it is
    /// <see cref="TugMoveBrakingLimitKts"/> at this margin that the limit curve starts from.</para>
    /// </summary>
    private static double TugMoveStopMarginFt(AircraftState mover)
    {
        AircraftCategory category = AircraftCategorization.Categorize(mover.AircraftType);
        double speedKts = CategoryPerformance.PushbackSpeed(category);
        double brakingKtSeconds = (speedKts * speedKts) / (2 * CategoryPerformance.TugDecelRate(category));
        return (brakingKtSeconds + (speedKts * DetectorIntervalSeconds)) * FtPerNm / 3600.0;
    }

    /// <summary>
    /// The tug move's remaining path swept against one parked or held neighbour (<see cref="GroundOutlineSweep.Sweep"/>),
    /// with the floor anchored to the move's start and to the row the tow began in. The row clearance is read from the
    /// tow's own measurements (<see cref="AircraftGroundOps.TowRowClearances"/>), measured once per neighbour for the tow
    /// rather than on every physics sub-tick.
    /// </summary>
    /// <param name="mover">The towed aircraft.</param>
    /// <param name="tugMove">Its running tug move.</param>
    /// <param name="obstacle">The neighbour.</param>
    /// <returns>What the sweep found.</returns>
    internal static GroundOutlineSweepResult TugMoveSweep(AircraftState mover, PushbackPhase tugMove, AircraftState obstacle)
    {
        var moverFootprint = AircraftFootprint.FromType(mover.AircraftType);
        var obstacleFootprint = AircraftFootprint.FromType(obstacle.AircraftType);
        IReadOnlyList<(PushbackPose Pose, double AlongFt)> path = tugMove.RemainingPath(mover, TugRunContinuation(mover));
        double? rowFt = mover.Ground.TowRowAnchor is { } anchor
            ? mover.Ground.TowRowClearances.RowClearanceFt(
                anchor,
                moverFootprint,
                obstacle.Callsign,
                new PushbackPose(obstacle.Position, obstacle.TrueHeading.Degrees),
                obstacleFootprint
            )
            : null;
        return GroundOutlineSweep.Sweep(
            path,
            tugMove.StartPose(mover),
            rowFt,
            new GroundOutlineFrame(mover.Position),
            GroundOutlineSize.Of(moverFootprint, towedNoseFirst: tugMove.Kind == PushbackLegKind.Pull),
            obstacle.Position,
            obstacle.TrueHeading.Degrees,
            GroundOutlineSize.Of(obstacleFootprint, towedNoseFirst: false)
        );
    }

    /// <summary>The row clearance as the outline diagnostic shows it; empty when it does not count.</summary>
    private static string RowText(GroundOutlineSweepResult swept) => swept.RowClearanceFt is { } rowFt ? $", row {rowFt:F1}ft" : "";

    /// <summary>
    /// Where a tug move fouls a parked or held neighbour: its <see cref="GroundOutline"/>, swept along the rest of its
    /// move (<see cref="PushbackPhase.RemainingPath"/>), may not come within <see cref="GroundOutlineSweep.WingtipBufferFt"/> of the
    /// neighbour's — or, for a neighbour the move already started closer to than that, as the aircraft on the next stand
    /// usually is, no closer than it was when the move began (or than the row the tow began in lets the wings pass,
    /// <see cref="AircraftGroundOps.TowRowAnchor"/>), less <see cref="GroundOutlineSweep.OutlineClearanceSlackFt"/>. Null when the
    /// whole move clears; otherwise how far along the remaining path the first fouled sample sits, which is what
    /// <see cref="ComputeClosingLimit"/> stops the move by.
    ///
    /// <para>The floor is anchored to the move's start pose (<see cref="PushbackPhase.StartPose"/>), not the live one. A
    /// floor read off the live pose follows the mover down: each pass allows the slack again, so a move stopped for a
    /// neighbour ratchets itself into contact a foot at a time. Anchored to the start, "no closer than it was" is a
    /// fixed line for the whole move, and the live pose is judged against it like every other sample.</para>
    ///
    /// <para>The floor never drops below <see cref="GroundOutlineSweep.OutlineClearanceSlackFt"/>, so contact is never passable: a mover
    /// whose outline already touches the neighbour's is held rather than released by a floor that has gone to zero. That
    /// is the runtime backstop for the command-time refusal of a move that starts inside a neighbour — a move that
    /// should never have been accepted must still not be driven any further by the tug.</para>
    ///
    /// <para>This replaces the half-wingspan lateral test for these pairs. That test compares the room along a straight
    /// line against two half-spans, so it held a straight push off a stand whose neighbour sits beside the tail although
    /// no part of either aircraft ever gets closer; and it cannot see a turn that swings the tail into the neighbour
    /// further along. A sample whose reference point is far enough away that no part of either outline can be under
    /// the floor is not measured.</para>
    /// </summary>
    private static double? TugMoveFoulsParkedAt(AircraftState mover, PushbackPhase tugMove, AircraftState obstacle, Action<string>? diagnosticLog)
    {
        GroundOutlineSweepResult swept = TugMoveSweep(mover, tugMove, obstacle);
        if (swept.Foul is not { } foul)
        {
            string closestText = swept.ClosestFt is { } closestFt ? $"{closestFt:F1}ft" : "nothing in reach";
            diagnosticLog?.Invoke(
                $"    [Outline] {mover.Callsign}→{obstacle.Callsign}: {tugMove.Kind} started {swept.StartClearanceFt:F1}ft off{RowText(swept)}, "
                    + $"closest {closestText} over {swept.SampleCount} samples ≥ floor({swept.FloorFt:F1}ft), passable"
            );
            return null;
        }

        diagnosticLog?.Invoke(
            $"    [Outline] {mover.Callsign}→{obstacle.Callsign}: {tugMove.Kind} started {swept.StartClearanceFt:F1}ft off{RowText(swept)}, sample "
                + $"{foul.SampleIndex}/{swept.SampleCount} {foul.AlongFt:F1}ft along {foul.ClearanceFt:F1}ft < floor({swept.FloorFt:F1}ft), "
                + $"crossing {foul.CrossingAlongFt:F1}ft along, in the way"
        );
        Log.LogDebug(
            "[Outline] {Callsign}: {Kind} move comes within {ClearanceFt:F1} ft of {Other} (started {StartFt:F1} ft off, "
                + "floor {FloorFt:F1} ft) at sample {Sample} of {Samples}, {AlongFt:F1} ft along, crossing the floor "
                + "{CrossingFt:F1} ft along",
            mover.Callsign,
            tugMove.Kind,
            foul.ClearanceFt,
            obstacle.Callsign,
            swept.StartClearanceFt,
            swept.FloorFt,
            foul.SampleIndex,
            swept.SampleCount,
            foul.AlongFt,
            foul.CrossingAlongFt
        );
        return foul.CrossingAlongFt;
    }

    /// <summary>
    /// A tug-moved aircraft giving way to a parked or held neighbour shows who it is yielding to
    /// (<see cref="AircraftGroundOps.AutoYieldTarget"/>), as a pusher stopped for traffic does — whether it is still
    /// braking for the neighbour or already stopped at it. Any outline limit under the speed the move is commanding from
    /// where it stands (<see cref="PushbackPhase.CommandedSpeedKts"/>) counts, not only the zero at the end of the curve:
    /// the tug is already slowing for the neighbour, and the operator sees what for from the first foot of it.
    ///
    /// <para>The commanded speed is the move's own, not <see cref="CategoryPerformance.PushbackSpeed"/>: a creep move
    /// runs its last stretch at <see cref="CategoryPerformance.PushbackAlignSpeed"/>, and a limit between the two is not
    /// slowing that move at all — annotating it would name a yield target for a tow going exactly as fast as it asked
    /// to.</para>
    ///
    /// <para>The limit caps the main gear, so it is compared with the gear speed the move commands
    /// (<see cref="PushbackPhase.CommandedGearSpeedKts"/>), not the tug's: mid-turn the gear runs slower than the tug, and a
    /// limit between the two is not slowing the tow either.</para>
    /// </summary>
    public static void ShowTugMoveYield(AircraftState mover, AircraftState obstacle, double limitKts)
    {
        if (TugMoveAgainstParked(mover, obstacle) is not { } tugMove)
        {
            return;
        }

        if (limitKts >= tugMove.CommandedGearSpeedKts(mover))
        {
            return;
        }

        mover.Ground.AutoYieldTarget = obstacle.Callsign;
        mover.Ground.AutoYieldIsFollowing = false;
    }

    /// <summary>
    /// How far under the speed the mover asks for a parked or held neighbour's limit must hold it before the yield label names
    /// that neighbour, knots: a marginal shave is not a hold anyone needs explaining.
    /// </summary>
    public const double ParkedHolderYieldMarginKts = 2.0;

    /// <summary>
    /// Caps <paramref name="mover"/> at a closing limit <paramref name="obstacle"/> imposes, naming the obstacle as the
    /// mover's yield target first when it is the parked or held aircraft holding it (<see cref="ShowParkedHolderYield"/>).
    /// </summary>
    private static void ApplyHolderLimit(
        AircraftState mover,
        (double Limit, string Reason, double? RoomFt, StopClosure Closure) result,
        AircraftState obstacle,
        double distFt
    )
    {
        ShowParkedHolderYield(mover, obstacle, result.Limit);
        ApplyMinLimit(mover, result.Limit, new LimitCause(result.Reason, obstacle, distFt, result.RoomFt, result.Closure));
    }

    /// <summary>
    /// Names a parked or held <paramref name="obstacle"/> as <paramref name="mover"/>'s yield target (<c>AutoYieldTarget</c>,
    /// not following) when the closing limit it imposes, <paramref name="limitKts"/>, is what holds the mover: a taxiing
    /// aircraft stopped or slowed beside a parked one shows what for, as a pusher's yield already does. Called before the
    /// limit is applied, so <see cref="AircraftGroundOps.SpeedLimit"/> is the binding limit of the pairs resolved so far.
    /// <list type="bullet">
    /// <item>The limit must hold the mover more than <see cref="ParkedHolderYieldMarginKts"/> under the speed it asks for now
    /// (<see cref="LiveRequestedSpeedKts"/>, a corner's speed in a corner).</item>
    /// <item>A stricter limit already binding the mover keeps its label: the label names the binding holder only.</item>
    /// <item>A yield another pair already named this pass (a moving lead, a convergence) stays unless this limit is
    /// stricter than the binding one.</item>
    /// </list>
    /// A tug-moved mover keeps the outline rule's own test (<see cref="ShowTugMoveYield"/>); a moving obstacle and a
    /// live-traffic shadow mover name nothing.
    /// </summary>
    /// <param name="mover">The aircraft the limit holds.</param>
    /// <param name="obstacle">The aircraft the limit is for.</param>
    /// <param name="limitKts">The closing limit, knots.</param>
    public static void ShowParkedHolderYield(AircraftState mover, AircraftState obstacle, double limitKts)
    {
        if (TugMoveAgainstParked(mover, obstacle) is not null)
        {
            ShowTugMoveYield(mover, obstacle, limitKts);
            return;
        }

        if (
            mover.IsShadow
            || !IsParkedOrHeld(obstacle)
            || (limitKts >= LiveRequestedSpeedKts(mover) - ParkedHolderYieldMarginKts)
            || !BindsAheadOfEarlierPairs(mover, limitKts)
        )
        {
            return;
        }

        mover.Ground.AutoYieldTarget = obstacle.Callsign;
        mover.Ground.AutoYieldIsFollowing = false;
    }

    /// <summary>
    /// Whether <paramref name="limitKts"/>, not yet applied, binds <paramref name="mover"/> ahead of the pairs resolved so far
    /// this pass: no limit yet, or one no stricter than it — strictly looser when an earlier pair already named a yield, so
    /// that pair keeps its label on a tie.
    /// </summary>
    private static bool BindsAheadOfEarlierPairs(AircraftState mover, double limitKts)
    {
        if (mover.Ground.SpeedLimit is not { } bound)
        {
            return true;
        }

        return (limitKts < bound) || ((limitKts <= bound) && (mover.Ground.AutoYieldTarget is null));
    }

    /// <summary>
    /// The speed <paramref name="mover"/> asks for now: its navigator's request before the ground speed limit
    /// (<see cref="GroundNavigator.RequestedSpeedKts"/>) while taxiing or following; else, and before the navigator first
    /// publishes one, the controller's taxi speed, else its category's default.
    /// </summary>
    private static double LiveRequestedSpeedKts(AircraftState mover)
    {
        double? navigator = mover.Phases?.CurrentPhase switch
        {
            TaxiingPhase taxiing => taxiing.NavRequestedSpeedKts,
            FollowingPhase following => following.NavRequestedSpeedKts,
            _ => null,
        };
        return navigator
            ?? mover.Ground.CommandedTaxiSpeedKts
            ?? CategoryPerformance.TaxiSpeed(AircraftCategorization.Categorize(mover.AircraftType));
    }

    private static void ApplyClosingLimit(
        AircraftState mover,
        double moveDir,
        AircraftState obstacle,
        MovementState obstacleState,
        double distFt,
        Action<string>? diagnosticLog
    )
    {
        if (ComputeClosingLimit(mover, moveDir, obstacle, obstacleState, distFt, diagnosticLog) is { } result)
        {
            ApplyMinLimit(mover, result.Limit, new LimitCause(result.Reason, obstacle, distFt, result.RoomFt, result.Closure));
        }
    }

    /// <summary>
    /// Resolve a Crossing pair (close in space, paths not on a shared edge or node).
    /// Real ground ops resolve a path conflict to "one holds, one goes" — never a
    /// symmetric mutual crawl, and never an indefinite creep (7110.65 3-7-2 HOLD/
    /// FOLLOW phraseology; AIM 4-3-18.b pilot give-way = a definite stop, not a
    /// crawl). Three rules:
    /// <list type="number">
    /// <item>An aircraft on the runway surface has priority — a plain ground crosser
    /// yields. Never strand an aircraft clearing the runway — it continues until the whole aircraft is past
    /// the hold line, into a ramp area if that is where it is going (AIM 4-3-21.b).</item>
    /// <item>Closing direction uses the aircraft's heading even when it is momentarily
    /// stopped (a taxiing/exiting aircraft that has braked for the conflict), so its
    /// hold stays latched instead of clearing and re-pinning each tick. Genuinely
    /// parked/held aircraft contribute no closing direction — they remain passable
    /// obstacles.</item>
    /// <item>If both would have to stop for each other (a crossing collision course),
    /// pick ONE holder (<see cref="ChooseMutualStopHolder"/>) and let the other proceed,
    /// instead of stopping both into a slow-motion gridlock.</item>
    /// </list>
    /// </summary>
    private static void ResolveCrossing(
        AircraftState a,
        MovementState stateA,
        AircraftState b,
        MovementState stateB,
        double distFt,
        bool routesKnown,
        Action<string>? diagnosticLog
    )
    {
        // Heading-based closing direction: a non-parked aircraft keeps a direction
        // even at gs=0 so a yielder that has braked for the conflict stays pinned.
        double? closeDirA = IsParkedOrHeld(a) ? null : a.TrueHeading.Degrees;
        double? closeDirB = IsParkedOrHeld(b) ? null : b.TrueHeading.Degrees;

        // Rule 1: the higher-ranked aircraft (a runway user, else one vacating a runway) proceeds; the other yields.
        var sideA = new CrossingSide(a, stateA, closeDirA, RankOf(a));
        var sideB = new CrossingSide(b, stateB, closeDirB, RankOf(b));
        if (TryYieldByRank(sideA, sideB, distFt, diagnosticLog))
        {
            return;
        }

        (double Limit, string Reason, double? RoomFt, StopClosure Closure)? limitForA = closeDirA is { } da
            ? ComputeClosingLimit(a, da, b, stateB, distFt, diagnosticLog)
            : null;
        (double Limit, string Reason, double? RoomFt, StopClosure Closure)? limitForB = closeDirB is { } db
            ? ComputeClosingLimit(b, db, a, stateA, distFt, diagnosticLog)
            : null;

        diagnosticLog?.Invoke(
            $"  [Crossing] {a.Callsign}(dir={closeDirA?.ToString("F0") ?? "none"},gs={a.GroundSpeed:F1})"
                + $"→limit={limitForA?.Limit.ToString("F1") ?? "null"} "
                + $"{b.Callsign}(dir={closeDirB?.ToString("F0") ?? "none"},gs={b.GroundSpeed:F1})"
                + $"→limit={limitForB?.Limit.ToString("F1") ?? "null"} dist={distFt:F0}ft"
        );

        if (limitForA is { Limit: <= 0 } && limitForB is { Limit: <= 0 })
        {
            // Hold one so the other proceeds instead of a mutual deadlock; ChooseMutualStopHolder picks
            // which. closeDirA/closeDirB are non-null here (a <= 0 limit was computed from each).
            AircraftState holder = ChooseMutualStopHolder(a, closeDirA!.Value, b, closeDirB!.Value);
            AircraftState mover = ReferenceEquals(holder, a) ? b : a;
            diagnosticLog?.Invoke($"  [Crossing] mutual stop: {holder.Callsign} holds, {mover.Callsign} proceeds");
            ApplyMinLimit(holder, 0, new LimitCause("crossing hold", mover, distFt, RoomFt: null, StopClosure.None));
        }
        else
        {
            if (limitForA is { } resultA)
            {
                diagnosticLog?.Invoke($"  [Crossing] one-sided: {a.Callsign} limited {resultA.Limit:F1} ({resultA.Reason}) for {b.Callsign}");
                ApplyHolderLimit(a, resultA, b, distFt);
            }
            if (limitForB is { } resultB)
            {
                diagnosticLog?.Invoke($"  [Crossing] one-sided: {b.Callsign} limited {resultB.Limit:F1} ({resultB.Reason}) for {a.Callsign}");
                ApplyHolderLimit(b, resultB, a, distFt);
            }
        }

        // Head-on fallback: two aircraft actually moving toward each other.
        if (closeDirA is { } da3 && closeDirB is { } db3 && a.GroundSpeed > 0 && b.GroundSpeed > 0)
        {
            ResolveHeadOn(a, da3, b, db3, distFt, arbitrate: routesKnown);
        }
    }

    /// <summary>One aircraft of a crossing pair: its movement state, closing direction (null when parked or held) and rank.</summary>
    private readonly record struct CrossingSide(AircraftState Ac, MovementState State, double? Dir, RunwayRank Rank);

    /// <summary>
    /// Rule 1 of <see cref="ResolveCrossing"/>: of two aircraft of different <see cref="RunwayRank"/>, the higher proceeds and the
    /// other, when a mover closing on it, yields at 0. True when the rule decided the pair (limit or none); false hands the pair
    /// to the normal closing treatment: equal ranks, a yielder with no direction (a parked obstacle keeps the normal treatment so
    /// the priority aircraft still stops rather than driving through it), or a pair <see cref="RuleOneBinds"/> leaves alone.
    /// </summary>
    private static bool TryYieldByRank(CrossingSide a, CrossingSide b, double distFt, Action<string>? diagnosticLog)
    {
        if (a.Rank == b.Rank)
        {
            return false;
        }

        (CrossingSide priority, CrossingSide yielder) = (a.Rank > b.Rank) ? (a, b) : (b, a);
        if ((yielder.Dir is not { } yielderDir) || !RuleOneBinds(priority, yielder, distFt))
        {
            return false;
        }

        bool conflict =
            ((priority.Dir is { } priorityDir) && IsClosingConflict(new ClosingPair(priority.Ac, priorityDir, yielder.Ac, yielder.State, distFt)))
            || IsClosingConflict(new ClosingPair(yielder.Ac, yielderDir, priority.Ac, priority.State, distFt));
        if (conflict)
        {
            bool onRunway = priority.Rank == RunwayRank.RunwayUser;
            diagnosticLog?.Invoke(
                $"  [Crossing] {priority.Ac.Callsign} {(onRunway ? "on runway" : "vacating runway")} → {yielder.Ac.Callsign} yields"
            );
            string reason = onRunway ? "yield to runway aircraft" : "yield to aircraft vacating the runway";
            ApplyMinLimit(yielder.Ac, 0, new LimitCause(reason, priority.Ac, distFt, RoomFt: null, StopClosure.None));
        }

        return true;
    }

    /// <summary>
    /// Whether Rule 1 binds <paramref name="yielder"/> to <paramref name="priority"/>. Always for a runway user. For a vacating
    /// aircraft its way out leads off the runway: moving, it gets priority unless the yielder lies ahead in its path or the
    /// crossing stop curve braked it in the last pass (<see cref="_crossingStopCurveCarried"/>), where the pair takes the normal
    /// treatment so the vacating aircraft keeps its own closing check (AIM 4-3-21.b lets it protrude, not collide). The carry
    /// is per aircraft: while the curve brakes a vacater it loses Rule 1 priority against every other aircraft, not only the
    /// traffic it is braking for, so it is never given the right of way and then loses the curve it was braking on; held
    /// (no direction), it gets priority only against traffic coming head-on into its way out — ahead along its heading inside the
    /// in-path corridor (<see cref="LiesInPath"/>), and closing, which Rule 1's own closing test then checks.
    /// Every other aircraft passing a held vacating one takes the normal closing check: it passes when clear, brakes to the stop
    /// ring, or stops at its own bar.
    /// </summary>
    private static bool RuleOneBinds(CrossingSide priority, CrossingSide yielder, double distFt)
    {
        if (priority.Rank != RunwayRank.Vacating)
        {
            return true;
        }

        return (priority.Dir is { } moving)
            ? (!LiesInPath(priority.Ac, moving, yielder.Ac, distFt) && (_crossingStopCurveCarried?.Contains(priority.Ac) != true))
            : LiesInPath(priority.Ac, priority.Ac.TrueHeading.Degrees, yielder.Ac, distFt);
    }

    private static void ResolveHeadOn(AircraftState a, double dirA, AircraftState b, double dirB, double distFt, bool arbitrate)
    {
        double headingDiff = HeadingDifference(dirA, dirB);
        if (headingDiff < HeadOnMinHeadingDiffDeg)
        {
            return;
        }

        if (distFt > OppositeStopDistanceFt)
        {
            return;
        }

        double bearingAtoB = GeoMath.BearingTo(a.Position, b.Position);
        if (HeadingDifference(dirA, bearingAtoB) >= 90)
        {
            return;
        }

        // Anti-parallel on two lanes far enough apart to pass abeam is a pass, not a head-on: neither
        // aircraft is on the other's track. A same-corridor head-on has ~no lateral offset and still holds.
        if (HasParallelTrackLateralRoom(a, dirA, b, dirB))
        {
            return;
        }

        if (arbitrate)
        {
            // The pair is a Crossing on the ground graph — i.e. they are on different, non-converging
            // edges, so their routes diverge past this point (a true same-corridor head-on would have
            // classified as SameEdgeHeadOn). Stopping BOTH gridlocks them — and a turning aircraft is
            // momentarily anti-parallel to a neighbour it will turn away from. Hold one and let the
            // other proceed (ChooseMutualStopHolder).
            AircraftState holder = ChooseMutualStopHolder(a, dirA, b, dirB);
            AircraftState mover = ReferenceEquals(holder, a) ? b : a;
            ApplyMinLimit(holder, 0, new LimitCause("head-on hold", mover, distFt, RoomFt: null, StopClosure.None));
            return;
        }

        // No ground graph to confirm diverging routes (e.g. off-graph): treat as a genuine collision
        // course and stop both.
        ApplyMinLimit(a, 0, new LimitCause("head-on", b, distFt, RoomFt: null, StopClosure.None));
        ApplyMinLimit(b, 0, new LimitCause("head-on", a, distFt, RoomFt: null, StopClosure.None));
    }

    // --- Helpers ---

    /// <summary>
    /// On the runway by phase evidence alone: landing, taking off, lining up, or rolling out on the centerline (a shadow: by
    /// the geometry stamped on it this pass). A generic holding-in-position aircraft is not.
    /// </summary>
    private static bool IsOnRunwayByPhase(AircraftState ac) =>
        ac.IsShadow ? ac.Ground.ExternalOnRunway : RunwayOccupancy.OccupiesSurface(RunwayOccupancy.ClassifyByPhase(ac, runway: null));

    /// <summary>
    /// The rank the phase alone gives: a runway user when on the runway by phase (<see cref="IsOnRunwayByPhase"/>), else the rest.
    /// </summary>
    private static RunwayRank FloorRank(AircraftState ac) => IsOnRunwayByPhase(ac) ? RunwayRank.RunwayUser : RunwayRank.Rest;

    /// <summary>The aircraft's rank in the pass in flight (<see cref="_runwayRanks"/>), else its phase-only rank.</summary>
    private static RunwayRank RankOf(AircraftState ac) =>
        ((_runwayRanks is { } ranks) && ranks.TryGetValue(ac, out RunwayRank rank)) ? rank : FloorRank(ac);

    /// <summary>
    /// The aircraft's <see cref="RunwayRank"/> against the pass layout's runways. With no layout or no runways, and for a shadow,
    /// the phase alone decides (<see cref="FloorRank"/>), as it does for an aircraft whose own runway is another airport's. At the
    /// pass airport: a LAHSO hold (<see cref="RunwayHoldingPhase"/>) and a stop inside the holding distance after an exit are
    /// vacating, never runway users; a line-up is a runway user only once its main gear is over its runway's strict pavement; a
    /// generic hold in position over any runway's strict pavement is vacating; and an aircraft not on a runway by its phase is
    /// vacating while <see cref="RunwayOccupancy.IsVacating"/> holds for any of the runways.
    /// </summary>
    private static RunwayRank ComputeRank(AircraftState ac, AirportGroundLayout? layout, IReadOnlyList<RunwayInfo> runways)
    {
        RunwayRank floor = FloorRank(ac);
        if (ac.IsShadow || (layout is null) || (runways.Count == 0))
        {
            return floor;
        }

        return PassAirportRank(ac, floor, layout, runways);
    }

    /// <summary>
    /// <see cref="ComputeRank"/> at the pass airport, by phase: a LAHSO hold, a stop inside the holding distance or a <c>CLRWY</c>
    /// hold with its tail inside the runway (<see cref="HoldingInPositionPhase.TailInsideRunway"/>) is vacating when
    /// the aircraft's own runway is here (else the floor); a line-up before its own runway's strict pavement is vacating or the
    /// rest; a generic hold in position over any runway's strict pavement is vacating; a runway user by phase stays one; anyone
    /// else is vacating or the rest (<see cref="VacatingOrRest"/>).
    /// </summary>
    private static RunwayRank PassAirportRank(AircraftState ac, RunwayRank floor, AirportGroundLayout layout, IReadOnlyList<RunwayInfo> runways)
    {
        RunwayInfo? own = ac.Phases?.CurrentPhaseRunway;
        RunwayInfo? ownHere = ((own is not null) && RunwayOccupancy.IsLayoutOf(layout, own)) ? own : null;
        return ac.Phases?.CurrentPhase switch
        {
            RunwayHoldingPhase
            or HoldingAfterExitPhase { StoppedInsideHoldingDistance: true }
            or HoldingInPositionPhase { TailInsideRunway: not null } => (ownHere is not null) ? RunwayRank.Vacating : floor,
            LineUpPhase when (ownHere is { } lineUpRunway) && !RunwayOccupancy.IsOverStrictPavement(ac.Position, lineUpRunway) => VacatingOrRest(
                ac,
                layout,
                runways
            ),
            HoldingInPositionPhase when IsOverAnyStrictPavement(ac, runways) => RunwayRank.Vacating,
            _ => (floor == RunwayRank.RunwayUser) ? floor : VacatingOrRest(ac, layout, runways),
        };
    }

    private static bool IsOverAnyStrictPavement(AircraftState ac, IReadOnlyList<RunwayInfo> runways) =>
        runways.Any(runway => RunwayOccupancy.IsOverStrictPavement(ac.Position, runway));

    private static RunwayRank VacatingOrRest(AircraftState ac, AirportGroundLayout layout, IReadOnlyList<RunwayInfo> runways)
    {
        var body = AircraftBody.Of(ac.AircraftType);
        return runways.Any(runway => RunwayOccupancy.IsVacating(ac, body, runway, layout)) ? RunwayRank.Vacating : RunwayRank.Rest;
    }

    /// <summary>
    /// Whether <paramref name="other"/> lies ahead in <paramref name="ac"/>'s path: less than 90° off its direction of travel and
    /// no farther to the side of its track than the two wingspans need to pass (<see cref="RequiredLateralClearanceFt"/>; with no
    /// wingspan on record for either, any aircraft ahead). Never for an aircraft with no direction (parked or held).
    /// </summary>
    private static bool LiesInPath(AircraftState ac, double? dir, AircraftState other, double distFt)
    {
        if (dir is not { } moveDir)
        {
            return false;
        }

        double offDeg = HeadingDifference(moveDir, GeoMath.BearingTo(ac.Position, other.Position));
        if (offDeg >= 90)
        {
            return false;
        }

        double lateralFt = distFt * Math.Sin(offDeg * Math.PI / 180.0);
        return (RequiredLateralClearanceFt(ac, other) is not { } requiredFt) || (lateralFt <= requiredFt);
    }

    /// <summary>
    /// True when the aircraft is intentionally stationary — parked, holding, or
    /// lining up — by phase. These aircraft are passable obstacles, not active
    /// movers, so they contribute no closing direction in crossing resolution.
    /// </summary>
    private static bool IsStationaryPhase(string? phaseName) =>
        phaseName is "At Parking" or "Holding After Pushback" or "Holding After Exit" or "Holding In Position" or "LinedUpAndWaiting" or "LiningUp"
        || (phaseName is not null && phaseName.StartsWith("Holding Short", StringComparison.Ordinal));

    /// <summary>
    /// True when the aircraft is parked, holding, lining up, or under any controller
    /// hold — and genuinely at rest (a passable obstacle). A taxiing or runway-exiting
    /// aircraft momentarily stopped for a conflict is NOT — it is yielding and keeps
    /// its heading-based closing direction. Conversely, a stationary-named phase that
    /// is actually rolling (a lining-up aircraft, a held aircraft still decelerating)
    /// is a mover, not an obstacle (#407, #409).
    /// </summary>
    internal static bool IsParkedOrHeld(AircraftState ac) =>
        IsParkedOrHeld(ac.Ground.IsImmobile, ac.Phases?.CurrentPhase?.Name, ac.GroundSpeed, ac.Targets.TargetSpeed);

    /// <summary>
    /// The same classification from the plain facts it reads, for a caller that holds no <see cref="AircraftState"/> —
    /// the client, which sees the other aircraft only as they arrive on the wire.
    /// </summary>
    /// <param name="isImmobile">The aircraft is under a controller hold.</param>
    /// <param name="phaseName">Its current phase's name, or null when it has none.</param>
    /// <param name="groundSpeedKts">Its ground speed, knots.</param>
    /// <param name="targetSpeedKts">The speed it is commanding, knots, or null when it commands none.</param>
    /// <returns>True when the aircraft is a passable obstacle at rest.</returns>
    public static bool IsParkedOrHeld(bool isImmobile, string? phaseName, double groundSpeedKts, double? targetSpeedKts) =>
        (isImmobile || IsStationaryPhase(phaseName)) && IsAtRest(groundSpeedKts, targetSpeedKts);

    private static (double StopFt, double TrailFt) GetSeparation(AircraftState leader, AircraftState trailer)
    {
        double stopDist = StopDistanceFt(AircraftLength.ResolveFt(leader.AircraftType), AircraftLength.ResolveFt(trailer.AircraftType));
        double trailDist = Math.Max(DefaultTrailDistanceFt, stopDist + 100.0);
        return (stopDist, trailDist);
    }

    /// <summary>
    /// The centre-to-centre distance (ft) of the stop ring between two aircraft <paramref name="lengthAFt"/> and
    /// <paramref name="lengthBFt"/> long: their half-lengths and <see cref="StopBufferFt"/>, never under
    /// <see cref="DefaultStopDistanceFt"/>.
    /// </summary>
    private static double StopDistanceFt(double lengthAFt, double lengthBFt) =>
        Math.Max(DefaultStopDistanceFt, ((lengthAFt + lengthBFt) / 2) + StopBufferFt);

    /// <summary>
    /// The gap (ft) the stop ring leaves between the facing ends of two aircraft <paramref name="lengthAFt"/> and
    /// <paramref name="lengthBFt"/> long (<see cref="StopDistanceFt"/> less their half-lengths): <see cref="StopBufferFt"/>, or
    /// more for a short pair the <see cref="DefaultStopDistanceFt"/> floor sets the ring of.
    /// </summary>
    public static double StopGapFt(double lengthAFt, double lengthBFt) => StopDistanceFt(lengthAFt, lengthBFt) - ((lengthAFt + lengthBFt) / 2);

    /// <summary>
    /// Limits <paramref name="trailer"/> behind <paramref name="leader"/> <paramref name="distFt"/> ahead: a stop inside the
    /// pair's stop ring. Outside it, a crosser (<see cref="HasCrossingStopCurve"/>) brakes along its crossing stop curve
    /// whenever that binds or holds its speed (<see cref="CrossingStopCurveKts"/>), inside the trail band or beyond it; any other
    /// trailer inside the band, and a crosser carried past its crossing whose curve neither binds nor holds, takes the trail match
    /// (the leader's ground speed). A crosser still in its crossing takes no trail match.
    /// </summary>
    private static void ApplyTrailLimit(AircraftState trailer, AircraftState leader, double distFt)
    {
        (double stopDist, double trailDist) = GetSeparation(leader, trailer);
        if (distFt <= stopDist)
        {
            ApplyMinLimit(trailer, 0, new LimitCause("trail stop", leader, distFt, RoomFt: null, StopClosure.None));
            return;
        }

        double roomFt = distFt - stopDist;
        if (HasCrossingStopCurve(trailer))
        {
            StopClosure closure = ClosureOf(leader, trailer);
            if (CrossingStopCurveKts(trailer, closure, roomFt) is { } curveKts)
            {
                ApplyMinLimit(trailer, curveKts, new LimitCause(CrossingStopCurveReason, leader, distFt, roomFt, closure));
                return;
            }
        }

        if ((distFt <= trailDist) && !IsCrossingRunway(trailer))
        {
            ApplyMinLimit(trailer, leader.GroundSpeed, new LimitCause("trail match", leader, distFt, roomFt, StopClosure.None));
        }
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/> brakes onto a stop ahead by the crossing stop curve (<see cref="CrossingStopCurveKts"/>):
    /// it is crossing a runway (<see cref="IsCrossingRunway"/>), or the curve limited it in the last pass
    /// (<see cref="_crossingStopCurveCarried"/>), so a crossing that ends behind traffic keeps braking along the curve rather than
    /// handing off to the proximity trail or a one-pass stop.
    /// </summary>
    private static bool HasCrossingStopCurve(AircraftState aircraft) =>
        IsCrossingRunway(aircraft) || (_crossingStopCurveCarried?.Contains(aircraft) == true);

    /// <summary>
    /// Whether <paramref name="aircraft"/> is crossing a runway: in a <see cref="CrossingRunwayPhase"/>, not merely past the
    /// near-line gate's commit point. A crossing takes no slowdown behind traffic on the runway, where it should not linger — by
    /// analogy with AIM 4-3-21.a, which says a pilot "should not stop or reverse course on the runway without first obtaining ATC
    /// approval": the trail match, the proximity trail and the convergence slowdown give it instead the braking onto the stop
    /// (<see cref="CrossingStopCurveKts"/>), and the detector's stops (the stop ring, a hold) still apply.
    /// </summary>
    private static bool IsCrossingRunway(AircraftState aircraft) => aircraft.Phases?.CurrentPhase is CrossingRunwayPhase;

    /// <summary>
    /// The limit (kt) a crosser (<see cref="HasCrossingStopCurve"/>) takes with <paramref name="roomFt"/> left before the stop
    /// ahead of it, with <paramref name="closure"/> the traffic's travel toward it while it stops (<see cref="ClosureOf"/>),
    /// whether or not the pair is inside the trail band (the closing check and the trail match consult it for any crosser
    /// outside the stop ring): the braking curve onto the stop (<see cref="StopCurveKts"/>) at the rate its stop needs over the
    /// whole room (<see cref="NeededStopRate"/>), no softer than its taxi brake rate and no harder than its firm rate
    /// (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>). That curve is at or above its ground speed only while a
    /// taxi-rate stop still fits, the stop beyond its own stopping distance: a crosser the curve did not bind in the last pass
    /// then keeps the speed the crossing is flying (null), and one it bound (<see cref="_crossingStopCurveCarried"/>) holds its
    /// ground speed, which carries the curve on, until it is released (<see cref="CrossingStopCurveReleases"/>): the traffic
    /// opening and the taxi-rate curve with <paramref name="closure"/> no longer binding, or the room exceeding its taxi-rate
    /// stop plus the traffic's travel the curve counted when it bound plus one pass's travel.
    ///
    /// <para>The rate is the one the stop needs rather than a switch between the taxi and the firm rate: the room shrinks by a
    /// pass's travel each pass, and switching to the firm rate on the pass the taxi-rate stop stops fitting would lift the curve
    /// above the ground speed and release a crosser the curve was holding. The caller passes the same room and closure to
    /// <see cref="ApplyMinLimit"/>, so the curve is floored at what the crosser's brakes shed in one pass
    /// (<see cref="FlooredLimitKts"/>).</para>
    ///
    /// <para>A crosser is clear of the runway only once its tail is past the far holding-position marking: AIM 4-3-21.b,
    /// "An aircraft is considered clear of the runway when all parts of the aircraft are past the runway edge and there are no
    /// restrictions to its continued movement beyond the runway holding position markings". So while it is still in its
    /// crossing with that point ahead (<see cref="CrossingRunwayPhase.TailClearRemainingFt"/>) and the obstacle closing on it
    /// (an obstacle not closing leaves the curve ending at the ring, which a floor would only move to the firm rate), a binding
    /// curve is held no lower than the stop curve onto that point (<see cref="TailClearFloorKts"/>) unless even the firm-rate
    /// closing curve is lower, in which case it stops on the runway along that one; and never above its ground speed, so a
    /// crosser the curve binds never speeds up. A stop (zero) is never raised.</para>
    /// </summary>
    private static double? CrossingStopCurveKts(AircraftState crosser, StopClosure closure, double roomFt)
    {
        AircraftCategory category = AircraftCategorization.Categorize(crosser.AircraftType);
        double rate = NeededStopRate(crosser.GroundSpeed, closure, roomFt, category);
        double curveKts = StopCurveKts(crosser.GroundSpeed, closure, rate, roomFt);
        if (curveKts >= crosser.GroundSpeed)
        {
            bool boundLastPass = _crossingStopCurveCarried?.Contains(crosser) == true;
            return (boundLastPass && !CrossingStopCurveReleases(crosser, closure, roomFt, category)) ? crosser.GroundSpeed : null;
        }

        return (curveKts > 0.0) && (closure.ClosingKts > 0.0) && (TailClearFloorKts(crosser, closure, roomFt) is { } floorKts)
            ? Math.Min(crosser.GroundSpeed, Math.Max(curveKts, floorKts))
            : curveKts;
    }

    /// <summary>
    /// Whether <paramref name="crosser"/>, held at its ground speed because the crossing stop curve bound it in an earlier pass
    /// and is now at or above that speed (<see cref="CrossingStopCurveKts"/>), is released. It is released when either holds:
    /// the traffic is opening (<see cref="StopClosure.Opening"/>) and the stop curve at its taxi brake rate over
    /// <paramref name="roomFt"/> with <paramref name="closure"/> (<see cref="StopCurveKts"/>, which takes one pass's travel of
    /// both aircraft off the room) no longer binds, at or above its ground speed; or <paramref name="roomFt"/> exceeds its
    /// taxi-rate stop from its ground speed, plus the traffic's travel toward it the curve counted when it bound, capped as it
    /// was then (<see cref="_crossingStopCurveBoundTravel"/>, as the pass started; zero for a crosser with none recorded), plus
    /// one pass's travel of both aircraft with <paramref name="closure"/>. Otherwise it stays held.
    /// </summary>
    private static bool CrossingStopCurveReleases(AircraftState crosser, StopClosure closure, double roomFt, AircraftCategory category)
    {
        double taxiRate = CategoryPerformance.TaxiDecelRate(category);
        if (closure.Opening && (StopCurveKts(crosser.GroundSpeed, closure, taxiRate, roomFt) >= crosser.GroundSpeed))
        {
            return true;
        }

        double boundTravelFt = _crossingStopCurveBoundTravel?.GetValueOrDefault(crosser) ?? 0.0;
        double passTravelFt = (crosser.GroundSpeed + closure.ClosingKts) * GroundStopBraking.FeetPerSecondPerKt * DetectorIntervalSeconds;
        return roomFt > GroundStopBraking.StoppingDistanceFt(crosser.GroundSpeed, taxiRate) + boundTravelFt + passTravelFt;
    }

    /// <summary>
    /// The floor (kt) under a binding crossing stop curve for <paramref name="crosser"/> still in its crossing with its tail
    /// short of the far holding-position marking (<see cref="CrossingRunwayPhase.TailClearRemainingFt"/>), else null: the plain
    /// stop curve (no closure) onto that point, at the taxi rate when a taxi-rate stop from its ground speed fits there and at
    /// the firm rate otherwise, but no higher than the closing curve at the firm rate over <paramref name="roomFt"/>, with
    /// <paramref name="closure"/> counted.
    /// </summary>
    private static double? TailClearFloorKts(AircraftState crosser, StopClosure closure, double roomFt)
    {
        if (
            (crosser.Phases?.CurrentPhase is not CrossingRunwayPhase crossing)
            || (crossing.TailClearRemainingFt(crosser.Position) is not { } tailClearFt)
            || (tailClearFt <= 0.0)
        )
        {
            return null;
        }

        AircraftCategory category = AircraftCategorization.Categorize(crosser.AircraftType);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(category);
        double taxiRate = CategoryPerformance.TaxiDecelRate(category);
        double tailClearRate = GroundStopBraking.StoppingDistanceFt(crosser.GroundSpeed, taxiRate) <= tailClearFt ? taxiRate : firmRate;
        double tailClearKts = StopCurveKts(crosser.GroundSpeed, StopClosure.None, tailClearRate, tailClearFt);
        double firmClosingKts = StopCurveKts(crosser.GroundSpeed, closure, firmRate, roomFt);
        return Math.Min(tailClearKts, firmClosingKts);
    }

    /// <summary>
    /// The speed (kt) whose stop at <paramref name="rate"/>, plus <paramref name="closure"/>'s travel toward the crosser while
    /// it stops, fits the room left once this pass's travel of both aircraft is taken off <paramref name="roomFt"/> (physics
    /// flies the limit until the next pass: read where the crosser stands, the limit trails it by a pass and it reaches the stop
    /// still rolling). An obstacle closing at c the whole stop: v²/(2·a) + c·v/a = room, so v = k / (c + √(c² + k)) with
    /// k = 2·a·room, which is √(2·a·room) for an obstacle not closing. A braking obstacle travels no further than its own stop
    /// (<see cref="StopClosure.StopFt"/>): once that caps its travel, v = √(2·a·(room − its stop)).
    /// </summary>
    private static double StopCurveKts(double groundSpeedKts, StopClosure closure, double rate, double roomFt)
    {
        double passTravelFt = (groundSpeedKts + closure.ClosingKts) * GroundStopBraking.FeetPerSecondPerKt * DetectorIntervalSeconds;
        double aheadFt = Math.Max(roomFt - passTravelFt, 0.0);
        if (closure.StopFt is { } obstacleStopFt)
        {
            double cappedKts = Math.Sqrt(2.0 * rate * Math.Max(aheadFt - obstacleStopFt, 0.0) / GroundStopBraking.FeetPerSecondPerKt);
            if (closure.TravelFt(cappedKts / rate) >= obstacleStopFt)
            {
                return cappedKts;
            }
        }

        double k = 2.0 * rate * aheadFt / GroundStopBraking.FeetPerSecondPerKt;
        return k > 0.0 ? k / (closure.ClosingKts + Math.Sqrt((closure.ClosingKts * closure.ClosingKts) + k)) : 0.0;
    }

    /// <summary>
    /// The rate (kt/s) a stop from <paramref name="groundSpeedKts"/> needs to fit <paramref name="roomFt"/> with
    /// <paramref name="closure"/>'s travel toward the stopping aircraft counted: a* = (GS²/2 + c·GS)·F / R, with F feet per
    /// second per knot; once the traffic's own stop S (<see cref="StopClosure.StopFt"/>) caps its travel, a* = GS²·F / (2·(R − S)),
    /// and the firm rate when R ≤ S. Taken over the whole room, never the room less a pass, and held between
    /// <paramref name="category"/>'s taxi brake rate and its firm rate (<see cref="CategoryPerformance.ExpediteExitDecelRate"/>):
    /// a stop that fits at the taxi rate brakes no softer than that, and one that needs more than the firm rate brakes at it.
    /// </summary>
    private static double NeededStopRate(double groundSpeedKts, StopClosure closure, double roomFt, AircraftCategory category)
    {
        double taxiRate = CategoryPerformance.TaxiDecelRate(category);
        double firmRate = CategoryPerformance.ExpediteExitDecelRate(category);
        if (roomFt <= 0.0)
        {
            return firmRate;
        }

        double stopTermFt = groundSpeedKts * groundSpeedKts / 2.0 * GroundStopBraking.FeetPerSecondPerKt;
        double closingTermFt = closure.ClosingKts * groundSpeedKts * GroundStopBraking.FeetPerSecondPerKt;
        double neededRate = (stopTermFt + closingTermFt) / roomFt;
        if ((neededRate > 0.0) && (closure.StopFt is { } obstacleStopFt) && (closingTermFt / neededRate > obstacleStopFt))
        {
            neededRate = roomFt > obstacleStopFt ? stopTermFt / (roomFt - obstacleStopFt) : firmRate;
        }

        return Math.Clamp(neededRate, taxiRate, firmRate);
    }

    /// <summary>
    /// Whether a stop from <paramref name="groundSpeedKts"/> at <paramref name="rate"/>, plus <paramref name="closure"/>'s
    /// travel toward the stopping aircraft while it stops, takes more than <paramref name="roomFt"/>.
    /// </summary>
    private static bool ClosingStopOverrunsRoom(double groundSpeedKts, double rate, StopClosure closure, double roomFt) =>
        GroundStopBraking.StoppingDistanceFt(groundSpeedKts, rate) + closure.TravelFt(groundSpeedKts / rate) > roomFt;

    /// <summary>
    /// How <paramref name="obstacle"/> closes on <paramref name="stopper"/> while it stops (<see cref="StopClosure"/>): its
    /// ground-speed component along the bearing from it to the stopper, in the direction physics moves it (its pushback heading
    /// under a push, else its heading), zero when it moves across or away; whether it is opening, that component pointing away
    /// from the stopper; and, when it is braking to a stop at the start of the pass, its projected stop, which caps its
    /// travel: it brakes along its move heading, so its travel toward the stopper before it stops is its own stopping
    /// distance from its ground speed times the cosine of that heading's angle off the bearing, never below zero. It brakes
    /// at the rate <see cref="StoppingRateOf"/> gives. An aircraft slowing to a positive target keeps moving, so its travel
    /// is not capped. Read from the state at the start of the pass, it is the same whichever pair the pass resolves first.
    /// </summary>
    public static StopClosure ClosureOf(AircraftState obstacle, AircraftState stopper)
    {
        double moveDeg = (obstacle.Ground.PushbackTrueHeading ?? obstacle.TrueHeading).Degrees;
        double offDeg = HeadingDifference(moveDeg, GeoMath.BearingTo(obstacle.Position, stopper.Position));
        double cosOff = Math.Cos(offDeg * Math.PI / 180.0);
        double towardKts = obstacle.GroundSpeed * cosOff;
        double closingKts = Math.Max(towardKts, 0.0);
        bool opening = towardKts < 0.0;
        if (StoppingRateOf(obstacle) is not { } obstacleRate)
        {
            return new StopClosure(closingKts, StopFt: null, Opening: opening);
        }

        double projectedStopFt = Math.Max(0.0, GroundStopBraking.StoppingDistanceFt(obstacle.GroundSpeed, obstacleRate) * cosOff);
        return new StopClosure(closingKts, projectedStopFt, Opening: opening);
    }

    /// <summary>
    /// The rate (kt/s) <paramref name="obstacle"/> brakes to a stop at, as <see cref="ClosureOf"/> reads it: its published
    /// decel rate when it publishes one toward a speed target of zero or below; else, when the detector braked it in its last
    /// pass (<see cref="_brakedLastPass"/>), the rate physics stops it at, its published decel rate or else its category's
    /// taxi brake rate; null when it is not braking to a stop.
    /// </summary>
    private static double? StoppingRateOf(AircraftState obstacle)
    {
        ControlTargets targets = obstacle.Targets;
        if ((targets.DesiredDecelRate is { } publishedRate) && (targets.TargetSpeed is { } targetKts) && (targetKts <= 0))
        {
            return publishedRate;
        }

        return (_brakedLastPass?.Contains(obstacle) == true)
            ? (targets.DesiredDecelRate ?? CategoryPerformance.TaxiDecelRate(AircraftCategorization.Categorize(obstacle.AircraftType)))
            : null;
    }

    /// <summary>
    /// Traffic closing on an aircraft that is stopping: its closing speed (kt), never below zero; for traffic braking to a
    /// stop, its projected stop (ft), its stopping distance along its move heading projected onto the line to the stopping
    /// aircraft, which caps how far it closes, null for traffic not braking to a stop; and whether it is opening, its
    /// ground-speed component along the bearing from it to the stopping aircraft pointing away (<see cref="ClosureOf"/>).
    /// </summary>
    public readonly record struct StopClosure(double ClosingKts, double? StopFt, bool Opening)
    {
        /// <summary>No traffic closing.</summary>
        public static StopClosure None => new(0.0, StopFt: null, Opening: false);

        /// <summary>How far (ft) the traffic closes over <paramref name="seconds"/>, no further than its own stop.</summary>
        public double TravelFt(double seconds) =>
            Math.Min(ClosingKts * seconds * GroundStopBraking.FeetPerSecondPerKt, StopFt ?? double.PositiveInfinity);
    }

    /// <summary>
    /// The limit (kt) a trail match (<see cref="ApplyTrailLimit"/>) sets on <paramref name="trailer"/> behind
    /// <paramref name="leader"/>: the leader's ground speed, floored as <see cref="ApplyMinLimit"/> floors it.
    /// </summary>
    private static double TrailMatchKts(AircraftState trailer, AircraftState leader, double distFt) =>
        FlooredLimitKts(trailer, leader.GroundSpeed, distFt - GetSeparation(leader, trailer).StopFt, StopClosure.None);

    /// <summary>
    /// Why a limit is set: the reason the log names, the aircraft it is set for, how far apart the two are (ft), the room (ft)
    /// left before the pair's stop ring when the limit's producer knows it (<see cref="FlooredLimitKts"/>), else null, and the
    /// other aircraft's travel toward this one while it stops that the limit counted: a crossing stop curve's closure
    /// (<see cref="CrossingStopCurveKts"/>), which its floor counts too, and <see cref="StopClosure.None"/> for every other limit.
    /// </summary>
    private readonly record struct LimitCause(string Reason, AircraftState Other, double DistFt, double? RoomFt, StopClosure Closure);

    /// <summary>
    /// Lowers <paramref name="aircraft"/>'s ground speed limit to <paramref name="maxSpeed"/>, keeping the lowest limit the
    /// pairs resolved so far this pass set. A non-zero limit is first floored (<see cref="FlooredLimitKts"/>) when its producer
    /// knows the room (<see cref="LimitCause.RoomFt"/>) left before the pair's stop ring; a stop (zero) stays instant. A crossing
    /// stop curve carries on to the next pass with the travel it counted (<see cref="CarryCrossingStopCurve"/>), and its floor
    /// chooses its rate with the closure the curve counted (<see cref="LimitCause.Closure"/>).
    /// </summary>
    private static void ApplyMinLimit(AircraftState aircraft, double maxSpeed, LimitCause cause)
    {
        if (aircraft.IsShadow)
        {
            return;
        }

        maxSpeed = FlooredLimitKts(aircraft, maxSpeed, cause.RoomFt, cause.Closure);
        if (cause.Reason is CrossingStopCurveReason or ConvergenceCrossingStopCurveReason)
        {
            CarryCrossingStopCurve(aircraft, maxSpeed, cause.Closure);
        }

        double? existing = aircraft.Ground.SpeedLimit;
        if (existing is { } ex)
        {
            aircraft.Ground.SpeedLimit = Math.Min(ex, maxSpeed);
        }
        else
        {
            aircraft.Ground.SpeedLimit = maxSpeed;
        }

        if (existing is null || maxSpeed < existing)
        {
            Log.LogDebug(
                "[Conflict] {Callsign}: limit={Limit:F0}kts, reason={Reason}, other={Other}, dist={Dist:F0}ft",
                aircraft.Callsign,
                maxSpeed,
                cause.Reason,
                cause.Other.Callsign,
                cause.DistFt
            );
            DebugSink?.Invoke(
                $"    [ApplyMinLimit] {aircraft.Callsign}: limit={maxSpeed:F1} (was {existing?.ToString("F1") ?? "none"}) "
                    + $"reason={cause.Reason} other={cause.Other.Callsign} dist={cause.DistFt:F0}ft"
            );
        }
    }

    /// <summary>
    /// Carries the crossing stop curve that limits <paramref name="aircraft"/> to <paramref name="limitKts"/> on to the next
    /// pass (<see cref="AircraftGroundOps.CrossingStopCurveCarry"/>), raising the closure travel it carries
    /// (<see cref="AircraftGroundOps.CrossingStopCurveBoundClosureTravelFt"/>) to the travel the curve counted:
    /// <paramref name="closure"/>'s travel toward it while it stops at its taxi brake rate from that limit, no further than the
    /// traffic's own stop (<see cref="StopClosure.TravelFt"/>).
    /// </summary>
    private static void CarryCrossingStopCurve(AircraftState aircraft, double limitKts, StopClosure closure)
    {
        double taxiRate = CategoryPerformance.TaxiDecelRate(AircraftCategorization.Categorize(aircraft.AircraftType));
        double travelFt = closure.TravelFt(limitKts / taxiRate);
        aircraft.Ground.CrossingStopCurveCarry = true;
        aircraft.Ground.CrossingStopCurveBoundClosureTravelFt = Math.Max(aircraft.Ground.CrossingStopCurveBoundClosureTravelFt, travelFt);
    }

    /// <summary>
    /// <paramref name="limitKts"/> raised to the speed <paramref name="aircraft"/> can still brake down from over the next
    /// detector pass (<see cref="BrakingFloorKts"/>, with <paramref name="roomFt"/> the room left before the pair's stop ring):
    /// physics clamps the speed to <see cref="AircraftGroundOps.SpeedLimit"/> at once, so a lower limit would brake the aircraft
    /// harder than its brakes allow. A stop (zero), and a limit whose producer knows no room (a tug move, floored at the towbar
    /// rate by <see cref="TugMoveLimit"/>), are returned as they are.
    /// </summary>
    private static double FlooredLimitKts(AircraftState aircraft, double limitKts, double? roomFt, StopClosure closure) =>
        (limitKts > 0.0) && (roomFt is { } room) ? Math.Max(limitKts, BrakingFloorKts(aircraft, room, closure)) : limitKts;

    private static double HeadingDifference(double h1, double h2)
    {
        double diff = Math.Abs(h1 - h2);
        if (diff > 180)
        {
            diff = 360 - diff;
        }
        return diff;
    }

    /// <summary>
    /// Picks which aircraft to HOLD when two same-priority movers would each stop for the other, in this order:
    /// <list type="number">
    /// <item>A live-traffic shadow is never the holder: the simulated aircraft is.</item>
    /// <item>The aircraft with the other clearly more dead ahead holds, its off-nose angle to the other smaller by at least
    /// <see cref="FollowerLeadOffNoseMarginDeg"/>, letting the other — which has it more abeam and moves away as it proceeds —
    /// go first (auto FOLLOW/BEHIND, 7110.65 3-7-2.a). It is the aircraft that must stop for the one in front of it, which
    /// includes a <c>FOLLOWG</c> follower with an aircraft nearly on its nose: the taxi clearance never relieves a pilot of
    /// collision avoidance (AIM 4-3-18.b).</item>
    /// <item>Otherwise, with exactly one of the pair in <see cref="FollowingPhase"/>, the follower keeps its place in its chain
    /// and the other aircraft holds.</item>
    /// <item>Otherwise the geometry is effectively symmetric and a deterministic callsign tie-break decides.</item>
    /// </list>
    /// <paramref name="dirA"/>/<paramref name="dirB"/> are the movement (closing) directions the caller already resolved.
    /// Deterministic; no oscillation.
    /// </summary>
    private static AircraftState ChooseMutualStopHolder(AircraftState a, double dirA, AircraftState b, double dirB)
    {
        // A shadow cannot be held (it moves as the real aircraft did): the simulated aircraft is always the holder.
        if (a.IsShadow != b.IsShadow)
        {
            return a.IsShadow ? b : a;
        }

        double offNoseA = HeadingDifference(dirA, GeoMath.BearingTo(a.Position, b.Position));
        double offNoseB = HeadingDifference(dirB, GeoMath.BearingTo(b.Position, a.Position));

        if (Math.Abs(offNoseA - offNoseB) >= FollowerLeadOffNoseMarginDeg)
        {
            return offNoseA < offNoseB ? a : b;
        }

        // One follower and one aircraft that is not following: the follower keeps its place in its chain and the other holds.
        bool aFollowing = IsFollowing(a);
        bool bFollowing = IsFollowing(b);
        if (aFollowing != bFollowing)
        {
            return aFollowing ? b : a;
        }

        return string.CompareOrdinal(a.Callsign, b.Callsign) >= 0 ? a : b;
    }

    /// <summary>Whether <paramref name="ac"/> is driving a <c>FOLLOWG</c> follow.</summary>
    private static bool IsFollowing(AircraftState ac) => ac.Phases?.CurrentPhase is FollowingPhase;

    /// <summary>
    /// Signed distance (nm) of <paramref name="ac"/> along <paramref name="seg"/> from its from-node — negative
    /// behind that node, positive past it, so farther along the shared edge is always a larger value, however far past
    /// the edge's end node the aircraft has gone.
    ///
    /// <para>Measured along the <b>chord</b> from the segment's from-node to its to-node, not along the edge's
    /// <see cref="DirectionalEdge.DepartureBearing"/>: on a fillet arc that bearing is the tangent at the from-node,
    /// so a projection onto it stops growing once the arc has turned 90° and falls away after — a leader 110° round a
    /// 118° fillet reads 0.94 R against a follower 80° round it at 0.985 R, the very inversion this ordering exists to
    /// fix. Signed progress along the chord rises monotonically all the way round a circular arc of under 180° of
    /// sweep, which is the property that orders an in-trail pair on a fillet.</para>
    ///
    /// <para>There is no degenerate-chord fallback because a segment's two nodes cannot coincide on a real layout:
    /// the parser drops a connector that lands on a taxiway vertex and the fillet generator clamps a cut arm clear of
    /// the 5 ft coincidence threshold, so no segment is ever shorter than that.</para>
    /// </summary>
    private static double AlongEdgeNm(AircraftState ac, TaxiRouteSegment seg)
    {
        LatLon from = seg.Edge.FromNode.Position;
        LatLon to = seg.Edge.ToNode.Position;
        return GeoMath.AlongTrackDistanceNm(ac.Position, from, new TrueHeading(GeoMath.BearingTo(from, to)));
    }

    internal static int? FindSharedUpcomingNode(TaxiRoute routeA, TaxiRoute routeB) =>
        FindSharedUpcomingNode(UpcomingNodes(routeA, ConvergenceLookaheadFt), routeB);

    /// <summary>
    /// The nodes <paramref name="route"/> reaches within <paramref name="lookaheadFt"/> of its current segment's from-node (the
    /// convergence look-ahead, or infinity for the whole route), each by the node it first enters it from.
    /// </summary>
    private static Dictionary<int, int> UpcomingNodes(TaxiRoute route, double lookaheadFt)
    {
        double lookaheadNm = lookaheadFt / FtPerNm;
        var nodes = new Dictionary<int, int>();
        double cumulative = 0;
        for (int i = route.CurrentSegmentIndex; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment seg = route.Segments[i];
            cumulative += seg.Edge.DistanceNm;
            if (cumulative > lookaheadNm)
            {
                break;
            }
            nodes.TryAdd(seg.ToNodeId, seg.FromNodeId);
        }

        return nodes;
    }

    /// <summary>
    /// The first node <paramref name="routeB"/> reaches within the look-ahead that is also in <paramref name="nodesA"/>
    /// (<see cref="UpcomingNodes"/>) and that the two enter from different nodes; null when there is none.
    /// </summary>
    private static int? FindSharedUpcomingNode(Dictionary<int, int> nodesA, TaxiRoute routeB)
    {
        double lookaheadNm = ConvergenceLookaheadFt / FtPerNm;
        double cumulativeB = 0;
        for (int i = routeB.CurrentSegmentIndex; i < routeB.Segments.Count; i++)
        {
            TaxiRouteSegment seg = routeB.Segments[i];
            cumulativeB += seg.Edge.DistanceNm;
            if (cumulativeB > lookaheadNm)
            {
                break;
            }

            if (nodesA.TryGetValue(seg.ToNodeId, out int fromA) && fromA != seg.FromNodeId)
            {
                return seg.ToNodeId;
            }
        }

        return null;
    }

    /// <summary>
    /// The aircraft in <paramref name="traffic"/> that can meet another on the taxiways, in its order: on the ground, driving a
    /// route (<see cref="FollowingPhase.DrivenRouteOf"/>; not parked, nor a follower waiting on its lead) with a current segment
    /// left, on a ground layout. Built once a step, for <see cref="MeetPointsOf"/> to pair.
    /// </summary>
    public static List<GroundMeetCandidate> MeetCandidates(IReadOnlyList<AircraftState> traffic)
    {
        var candidates = new List<GroundMeetCandidate>();
        foreach (AircraftState ac in traffic)
        {
            if (
                ac.IsOnGround
                && (FollowingPhase.DrivenRouteOf(ac) is { CurrentSegment: { } segment } route)
                && (ac.Ground.Layout?.AirportId is { } airportId)
            )
            {
                candidates.Add(new GroundMeetCandidate(ac, route, segment.Edge.FromNode.Position, airportId));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Where <paramref name="own"/>'s driven route meets the others' in <paramref name="candidates"/>: one row per aircraft whose
    /// route shares a junction with its own within the convergence look-ahead (<see cref="FindSharedUpcomingNode(TaxiRoute, TaxiRoute)"/>),
    /// at that junction, in <paramref name="candidates"/>' order. An aircraft that is not a
    /// candidate (parked, airborne) is no one's partner. Null when there is no row. It predicts where the routes meet, not who
    /// gives way or whether either has to slow.
    /// </summary>
    public static IReadOnlyList<GroundMeetPointDto>? MeetPointsOf(GroundMeetCandidate own, IReadOnlyList<GroundMeetCandidate> candidates)
    {
        Dictionary<int, int>? ownNodes = null;
        List<GroundMeetPointDto>? rows = null;
        foreach (GroundMeetCandidate other in candidates)
        {
            if (!CanMeet(own, other))
            {
                continue;
            }

            ownNodes ??= UpcomingNodes(own.Route, ConvergenceLookaheadFt);
            if (FindSharedUpcomingNode(ownNodes, other.Route) is { } nodeId)
            {
                rows ??= [];
                rows.Add(new GroundMeetPointDto(other.Aircraft.Callsign, nodeId, JunctionPosition(own, other, nodeId)));
            }
        }

        return rows;
    }

    /// <summary>
    /// Whether the pair is worth walking: two different aircraft on the same airport's layout (node ids are per layout) whose route
    /// starts lie within <see cref="MeetPairRangeFt"/>. Each route's look-ahead counts from its current segment's from-node, which a
    /// fresh taxi route can place well ahead of the aircraft, so a junction both reach within it lies within the look-ahead of both
    /// from-nodes, and from-nodes farther apart than twice it cannot share one.
    /// </summary>
    private static bool CanMeet(GroundMeetCandidate own, GroundMeetCandidate other) =>
        (!ReferenceEquals(own.Aircraft, other.Aircraft))
        && (own.AirportId == other.AirportId)
        && (GeoMath.DistanceNm(own.RouteStart, other.RouteStart) * FtPerNm <= MeetPairRangeFt);

    /// <summary>The position of the junction <paramref name="nodeId"/> ahead on <paramref name="own"/>'s route.</summary>
    private static LatLon JunctionPosition(GroundMeetCandidate own, GroundMeetCandidate other, int nodeId)
    {
        int index = own.Route.SegmentIndexAhead(nodeId);
        if (index == int.MaxValue)
        {
            throw new InvalidOperationException(
                $"{own.Aircraft.Callsign}: shared node {nodeId} with {other.Aircraft.Callsign} is not on its route ahead"
            );
        }

        return own.Route.Segments[index].Edge.ToNode.Position;
    }

    /// <summary>The farthest apart (ft) two route starts can lie and still have routes meeting within the look-ahead.</summary>
    private const double MeetPairRangeFt = 2.0 * ConvergenceLookaheadFt;

    /// <summary>
    /// Where <paramref name="held"/> stops giving way to <paramref name="target"/>: how far (ft) it still has to taxi along its
    /// route before its centre comes within the pair's lateral clearance of the target's track through the junction, or its
    /// nose comes within the target's half of it, whichever comes first, with the junction node. The junctions are the nodes
    /// where the routes meet across each other that the target has still to clear (<see cref="UnclearedCrossings"/>, judged as
    /// <see cref="GiveWayJunctionOf"/> judges the GIVEWAY release), however far along either route, and the stop is the one
    /// the held aircraft comes to first along its route; each junction's track is the target's route edges into and out of
    /// that node; the centre's clearance is both half-spans plus
    /// <see cref="GroundOutlineSweep.WingtipBufferFt"/> (<see cref="RequiredLateralClearanceFt"/>, the room
    /// <see cref="ComputeClosingLimit"/> lets the target pass a held aircraft with); the nose — the centre projected half the
    /// held aircraft's length ahead (<see cref="AircraftLength.ResolveFt"/>) — keeps the target's half-span plus the buffer,
    /// so a long type stops farther back than its centre alone needs. Spans are the category spans of
    /// <see cref="AircraftFootprint.ResolveWingspanFt"/> when the FAA database lacks either aircraft's. Zero when the centre or the
    /// nose is already that close.
    ///
    /// <para>
    /// Null with no stop point, <paramref name="noStopReason"/> saying which: the held aircraft has no route; the target has
    /// no route; or the routes share no node the target has still to clear. Null reason with a stop point.
    /// </para>
    /// </summary>
    internal static (int NodeId, double ToStopFt)? GiveWayStop(AircraftState held, AircraftState target, out string? noStopReason)
    {
        if (FollowingPhase.DrivenRouteOf(held) is not { } heldRoute)
        {
            noStopReason = "no route of its own";
            return null;
        }

        if (FollowingPhase.DrivenRouteOf(target) is not { } targetRoute)
        {
            noStopReason = "the traffic has no route";
            return null;
        }

        (int NodeId, double ToStopFt)? first = null;
        foreach (GiveWayJunction junction in UnclearedCrossings(held, target))
        {
            List<DirectionalEdge> track = TrackThroughRouteNode(targetRoute, junction.TrafficNodeIndex);
            if (StopShortOfTrackFt(held, heldRoute, junction.NodeId, track, target, double.PositiveInfinity) is not { } toStopFt)
            {
                throw new InvalidOperationException(
                    $"{held.Callsign}: shared node {junction.NodeId} with {target.Callsign} is not on its route ahead"
                );
            }

            if ((first is not { } nearest) || (toStopFt < nearest.ToStopFt))
            {
                first = (junction.NodeId, toStopFt);
            }
        }

        noStopReason = (first is null) ? "no node shared with the traffic's route that the traffic has still to clear" : null;
        return first;
    }

    /// <summary>
    /// Where a <c>FOLLOWG</c> follower <paramref name="held"/> stops giving way to its <paramref name="lead"/> short of the
    /// merge, placed as the GIVEWAY overload above places its stop: how far (ft) it still has to taxi along
    /// <paramref name="heldRoute"/>, its route to the merge, before its centre or nose comes inside the pair's clearance of
    /// <paramref name="leadTrack"/>, the lead's path edges into and out of the merge node; with the merge node, where the
    /// route ends. Zero when the centre or the nose is already that close.
    ///
    /// <para>
    /// Null with no stop point, <paramref name="noStopReason"/> saying which: the route has no segment left to the merge; or
    /// the merge lies beyond <see cref="ConvergenceLookaheadFt"/> along it. Null reason with a stop point.
    /// </para>
    /// </summary>
    internal static (int NodeId, double ToStopFt)? GiveWayStop(
        AircraftState held,
        TaxiRoute heldRoute,
        IReadOnlyList<DirectionalEdge> leadTrack,
        AircraftState lead,
        out string? noStopReason
    )
    {
        if (heldRoute.IsComplete)
        {
            noStopReason = "no route left to the merge";
            return null;
        }

        int mergeNodeId = heldRoute.Segments[^1].ToNodeId;
        if (StopShortOfTrackFt(held, heldRoute, mergeNodeId, leadTrack, lead, ConvergenceLookaheadFt) is not { } toStopFt)
        {
            noStopReason = "the merge is beyond the 1,500 ft look-ahead along its route";
            return null;
        }

        noStopReason = null;
        return (mergeNodeId, toStopFt);
    }

    /// <summary>
    /// How far (ft) <paramref name="held"/> taxis along <paramref name="heldRoute"/>, no further than
    /// <paramref name="nodeId"/>, before it comes inside the room it leaves <paramref name="target"/>'s
    /// <paramref name="track"/> (<see cref="GiveWayClearance"/>, sized from both aircraft); null when the walk ends on
    /// <paramref name="lookaheadFt"/> short of the node, or the node is not on the route ahead
    /// (<see cref="DistanceToTrackClearanceFt"/>).
    /// </summary>
    private static double? StopShortOfTrackFt(
        AircraftState held,
        TaxiRoute heldRoute,
        int nodeId,
        IReadOnlyList<DirectionalEdge> track,
        AircraftState target,
        double lookaheadFt
    )
    {
        bool faaSpans = RequiredLateralClearanceFt(held, target) is not null;
        double heldHalfSpanFt = HalfSpanFt(held.AircraftType, faaSpans);
        double targetHalfSpanFt = HalfSpanFt(target.AircraftType, faaSpans);
        var clearance = new GiveWayClearance(
            track,
            heldHalfSpanFt + targetHalfSpanFt + GroundOutlineSweep.WingtipBufferFt,
            targetHalfSpanFt + GroundOutlineSweep.WingtipBufferFt,
            AircraftLength.ResolveFt(held.AircraftType) / 2.0
        );
        return DistanceToTrackClearanceFt(held, heldRoute, nodeId, clearance, lookaheadFt);
    }

    /// <summary>The route's edges into and out of its node <paramref name="nodeIndex"/> (<see cref="RouteNodeId"/>), whichever it has.</summary>
    private static List<DirectionalEdge> TrackThroughRouteNode(TaxiRoute route, int nodeIndex)
    {
        var track = new List<DirectionalEdge>(2);
        if (nodeIndex > 0)
        {
            track.Add(route.Segments[nodeIndex - 1].Edge);
        }

        if (nodeIndex < route.Segments.Count)
        {
            track.Add(route.Segments[nodeIndex].Edge);
        }

        return track;
    }

    /// <summary>
    /// Where a GIVEWAY's two routes meet across each other: the node the traffic's route shares with what is left of the held
    /// aircraft's (<see cref="GiveWayJunctionOf"/>), its index on the traffic's route (<see cref="RouteNodeId"/>), how far
    /// (ft) the traffic's tail is past it along its route (negative while short of it), how far past it the tail must be to
    /// clear the held aircraft (its half-span plus <see cref="GroundOutlineSweep.WingtipBufferFt"/>), and whether the
    /// traffic's remaining route runs head-on along the held route toward the held aircraft
    /// (<see cref="RunsHeadOnAlongHeldRoute"/>), which leaves a cleared node to the opposite-direction judgement of
    /// <see cref="FlightPhysics.IsGiveWayMet"/>.
    /// </summary>
    internal readonly record struct GiveWayJunction(int NodeId, int TrafficNodeIndex, double TailPastFt, double RequiredPastFt, bool TrafficHeadOn)
    {
        /// <summary>Whether the traffic's tail has cleared the node: the give-way is met.</summary>
        internal bool TrafficPast => TailPastFt >= RequiredPastFt;
    }

    /// <summary>
    /// The one judgement of where <paramref name="held"/>, giving way to <paramref name="traffic"/>, waits for it: the node both
    /// driven routes pass that the traffic meets across the held aircraft's path (<see cref="CrossesHeldRoute"/>), over the
    /// whole of both routes rather than the convergence look-ahead. Traffic ahead on the held aircraft's taxiway (which
    /// in-trail spacing handles) or head-on along it (which the pass geometry of <see cref="FlightPhysics.IsGiveWayMet"/>
    /// handles) meets it at no such node.
    ///
    /// <para>
    /// The node returned is the first one the traffic has still to clear: the nearest it has passed, when its tail has not yet
    /// made the clearance there, else the first ahead on its route. With none left to clear, the nearest it has cleared
    /// (<see cref="GiveWayJunction.TrafficPast"/>, the give-way met). Null when either aircraft drives no route
    /// (<see cref="FollowingPhase.DrivenRouteOf"/>: parked, pushing, a follower waiting on its lead) or the routes share no
    /// such node.
    /// </para>
    /// </summary>
    internal static GiveWayJunction? GiveWayJunctionOf(AircraftState held, AircraftState traffic) =>
        (GiveWayRoutes.Of(held, traffic) is { } routes) ? FirstJunction(routes) : null;

    /// <summary>
    /// <see cref="GiveWayJunctionOf(AircraftState, AircraftState)"/> with <paramref name="heldRoute"/> as the held aircraft's route, driven
    /// or not: the route a deferred TAXI would build. The route is read for its nodes only, so the held aircraft need not be on it.
    /// </summary>
    internal static GiveWayJunction? GiveWayJunctionOf(AircraftState held, AircraftState traffic, TaxiRoute heldRoute) =>
        (GiveWayRoutes.Of(held, traffic, heldRoute) is { } routes) ? FirstJunction(routes) : null;

    /// <summary>
    /// What <paramref name="junction"/> says of a GIVEWAY: whether the traffic's tail has cleared the node, and null when no node
    /// decides it, with no junction or with a cleared one the traffic's route then runs head-on along the held route from. A null
    /// leaves the judgement to the pass geometry of <see cref="FlightPhysics.IsGiveWayMet"/>, which a deferred GIVEWAY and a queued
    /// GiveWay block apply only once the traffic no longer runs head-on along the route (<see cref="GiveWayTrafficHeadOn"/>).
    /// </summary>
    internal static bool? GiveWayJunctionClear(GiveWayJunction? junction) =>
        ((junction is { } met) && !(met.TrafficPast && met.TrafficHeadOn)) ? met.TrafficPast : null;

    /// <summary>
    /// Whether <paramref name="traffic"/>'s driven route, from its current segment on, runs head-on along <paramref name="heldRoute"/>
    /// toward <paramref name="held"/> (<see cref="RunsHeadOnAlongHeldRoute"/>), at any distance; false when the traffic drives no
    /// route. Read from its current segment, so it turns false once the traffic is past.
    /// </summary>
    internal static bool GiveWayTrafficHeadOn(AircraftState held, AircraftState traffic, TaxiRoute heldRoute) =>
        GiveWayRoutes.Of(held, traffic, heldRoute) is { TrafficHeadOn: true };

    private static GiveWayJunction? FirstJunction(GiveWayRoutes routes)
    {
        GiveWayJunction? cleared = null;
        for (int k = routes.Current; k >= 0; k--)
        {
            if (routes.JunctionAt(k) is { } junction)
            {
                if (!junction.TrafficPast)
                {
                    return junction;
                }

                cleared = junction;
                break;
            }
        }

        for (int k = routes.Current + 1; k <= routes.TrafficRoute.Segments.Count; k++)
        {
            if (routes.JunctionAt(k) is { } junction)
            {
                return junction;
            }
        }

        return cleared;
    }

    /// <summary>
    /// Every node where <paramref name="traffic"/>'s route meets <paramref name="held"/>'s across it that the traffic has still to
    /// clear, in its route's order from behind it: those it has passed whose clearance its tail has not yet made, back to the
    /// nearest it has cleared, then every one ahead. Empty when <see cref="GiveWayJunctionOf"/> finds none to clear.
    /// </summary>
    private static List<GiveWayJunction> UnclearedCrossings(AircraftState held, AircraftState traffic)
    {
        var uncleared = new List<GiveWayJunction>();
        if (GiveWayRoutes.Of(held, traffic) is not { } routes)
        {
            return uncleared;
        }

        for (int k = routes.Current; k >= 0; k--)
        {
            if (routes.JunctionAt(k) is { } junction)
            {
                if (junction.TrafficPast)
                {
                    break;
                }

                uncleared.Insert(0, junction);
            }
        }

        for (int k = routes.Current + 1; k <= routes.TrafficRoute.Segments.Count; k++)
        {
            if (routes.JunctionAt(k) is { } junction)
            {
                uncleared.Add(junction);
            }
        }

        return uncleared;
    }

    /// <summary>
    /// The two driven routes of a GIVEWAY as <see cref="GiveWayJunctionOf"/> judges them: the held route's nodes ahead, each by
    /// the node it enters it from (<see cref="UpcomingNodes"/> over the whole route, walked once per judgement), the traffic's
    /// route and its current segment, and how far past a node the traffic's tail must be to clear the held aircraft.
    /// </summary>
    private sealed class GiveWayRoutes(AircraftState traffic, TaxiRoute heldRoute, TaxiRoute trafficRoute, double requiredPastFt)
    {
        private readonly Dictionary<int, int> _heldNodes = UpcomingNodes(heldRoute, double.PositiveInfinity);
        private readonly double _halfLengthFt = AircraftLength.ResolveFt(traffic.AircraftType) / 2.0;

        /// <summary>The traffic's driven route.</summary>
        internal TaxiRoute TrafficRoute { get; } = trafficRoute;

        /// <summary>The traffic's current segment, clamped to its route: node <c>Current</c> is the last it passed.</summary>
        internal int Current { get; } = Math.Clamp(trafficRoute.CurrentSegmentIndex, 0, trafficRoute.Segments.Count);

        /// <summary>
        /// Whether the traffic's route, from its current segment on, runs head-on along the held route
        /// (<see cref="RunsHeadOnAlongHeldRoute"/>).
        /// </summary>
        internal bool TrafficHeadOn => RunsHeadOnAlongHeldRoute(heldRoute, _heldNodes, TrafficRoute);

        /// <summary>The pair's routes; null when either aircraft drives no route, or the traffic's has no segment.</summary>
        internal static GiveWayRoutes? Of(AircraftState held, AircraftState traffic) =>
            (FollowingPhase.DrivenRouteOf(held) is { } heldRoute) ? Of(held, traffic, heldRoute) : null;

        /// <summary>
        /// The pair's routes with <paramref name="heldRoute"/> as the held aircraft's; null when the traffic drives no route with a
        /// segment.
        /// </summary>
        internal static GiveWayRoutes? Of(AircraftState held, AircraftState traffic, TaxiRoute heldRoute)
        {
            if (FollowingPhase.DrivenRouteOf(traffic) is not { Segments.Count: > 0 } route)
            {
                return null;
            }

            bool faaSpans = RequiredLateralClearanceFt(held, traffic) is not null;
            double requiredPastFt = HalfSpanFt(held.AircraftType, faaSpans) + GroundOutlineSweep.WingtipBufferFt;
            return new GiveWayRoutes(traffic, heldRoute, route, requiredPastFt);
        }

        /// <summary>The junction at the traffic route's node <paramref name="nodeIndex"/>; null when the routes do not meet across each other there.</summary>
        internal GiveWayJunction? JunctionAt(int nodeIndex) =>
            CrossesHeldRoute(heldRoute, _heldNodes, TrafficRoute, nodeIndex)
                ? new GiveWayJunction(
                    RouteNodeId(TrafficRoute, nodeIndex),
                    nodeIndex,
                    CentrePastRouteNodeFt(traffic, TrafficRoute, nodeIndex) - _halfLengthFt,
                    requiredPastFt,
                    TrafficHeadOn
                )
                : null;
    }

    /// <summary>
    /// Whether the traffic, on <paramref name="route"/>, meets the held route across it at its node <paramref name="nodeIndex"/>:
    /// the node is ahead on the held route (<paramref name="heldNodes"/>, each by the node the held aircraft enters it from) and
    /// the traffic's other node there — the one it enters from, or, at the route's first node, which it starts on, the one it
    /// leaves for — is neither that node nor the one the held route leaves it for.
    ///
    /// <para>
    /// A rejoin is no crossing (<see cref="RejoinsHeldRouteInTrail"/>): where the traffic, in trail on the held route, takes a
    /// different way between two of its nodes (a square corner against the held route's fillet) and comes back onto it at the
    /// node leaving it the way the held route does, the two are still in trail there.
    /// </para>
    /// </summary>
    private static bool CrossesHeldRoute(TaxiRoute heldRoute, Dictionary<int, int> heldNodes, TaxiRoute route, int nodeIndex)
    {
        int nodeId = RouteNodeId(route, nodeIndex);
        if (!heldNodes.TryGetValue(nodeId, out int heldFrom))
        {
            return false;
        }

        int trafficOther = (nodeIndex == 0) ? route.Segments[0].ToNodeId : route.Segments[nodeIndex - 1].FromNodeId;
        if ((trafficOther == heldFrom) || (trafficOther == HeldNextNode(heldRoute, nodeId)))
        {
            return false;
        }

        return !RejoinsHeldRouteInTrail(heldRoute, heldNodes, route, nodeIndex);
    }

    /// <summary>
    /// Whether the traffic, on <paramref name="route"/>, comes back onto the held route at its node <paramref name="nodeIndex"/>
    /// in trail: it leaves the node for the one the held route leaves it for, and the last node before it that it shares with
    /// the held route (<paramref name="heldNodes"/>) is one the held route reaches first (an earlier
    /// <see cref="TaxiRoute.SegmentIndexAhead"/>) and that the traffic enters from the node the held route enters it from. A
    /// route that starts there has no such entry.
    /// </summary>
    private static bool RejoinsHeldRouteInTrail(TaxiRoute heldRoute, Dictionary<int, int> heldNodes, TaxiRoute route, int nodeIndex)
    {
        int nodeId = RouteNodeId(route, nodeIndex);
        bool leavesTheHeldWay = (nodeIndex < route.Segments.Count) && (route.Segments[nodeIndex].ToNodeId == HeldNextNode(heldRoute, nodeId));
        if (!leavesTheHeldWay)
        {
            return false;
        }

        for (int k = nodeIndex - 1; k >= 0; k--)
        {
            int previousId = RouteNodeId(route, k);
            if (heldNodes.TryGetValue(previousId, out int heldFrom))
            {
                bool heldReachesItFirst = heldRoute.SegmentIndexAhead(previousId) < heldRoute.SegmentIndexAhead(nodeId);
                bool entersItTheHeldWay = (k > 0) && (route.Segments[k - 1].FromNodeId == heldFrom);
                return heldReachesItFirst && entersItTheHeldWay;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="route"/>, from its current segment on, runs head-on along the held route toward the held aircraft
    /// anywhere: it enters a node ahead on the held route (<paramref name="heldNodes"/>) from the node the held route leaves it
    /// for, or leaves such a node for the one the held aircraft enters it from.
    /// </summary>
    private static bool RunsHeadOnAlongHeldRoute(TaxiRoute heldRoute, Dictionary<int, int> heldNodes, TaxiRoute route)
    {
        for (int i = Math.Max(route.CurrentSegmentIndex, 0); i < route.Segments.Count; i++)
        {
            TaxiRouteSegment segment = route.Segments[i];
            bool entersFromHeldNext = heldNodes.ContainsKey(segment.ToNodeId) && (HeldNextNode(heldRoute, segment.ToNodeId) == segment.FromNodeId);
            bool leavesTowardHeldPrevious = heldNodes.TryGetValue(segment.FromNodeId, out int heldFrom) && (heldFrom == segment.ToNodeId);
            if (entersFromHeldNext || leavesTowardHeldPrevious)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The node <paramref name="heldRoute"/> goes to after <paramref name="nodeId"/>, a node ahead on it; null where it ends there.</summary>
    private static int? HeldNextNode(TaxiRoute heldRoute, int nodeId)
    {
        int heldIndex = heldRoute.SegmentIndexAhead(nodeId);
        return (heldIndex < heldRoute.Segments.Count - 1) ? heldRoute.Segments[heldIndex + 1].ToNodeId : null;
    }

    /// <summary>The route's node <paramref name="nodeIndex"/>: 0 the first segment's from-node, k the k-th segment's to-node.</summary>
    private static int RouteNodeId(TaxiRoute route, int nodeIndex) =>
        (nodeIndex == 0) ? route.Segments[0].FromNodeId : route.Segments[nodeIndex - 1].ToNodeId;

    /// <summary>
    /// How far (ft) <paramref name="aircraft"/>'s centre is past its route's node <paramref name="nodeIndex"/>
    /// (<see cref="RouteNodeId"/>), negative while short of it. A node at or behind the node it last passed (its current
    /// segment's from-node, or the route's last node once the route is complete): the signed along-track distance from that
    /// node along the segment's chord (<see cref="AlongEdgeNm"/>, negative while the aircraft is still short of it), plus the
    /// edges from this node to that one. A node ahead: straight from the aircraft to its current segment's end node, plus the
    /// edges from there to this one.
    /// </summary>
    private static double CentrePastRouteNodeFt(AircraftState aircraft, TaxiRoute route, int nodeIndex)
    {
        int count = route.Segments.Count;
        int current = Math.Clamp(route.CurrentSegmentIndex, 0, count);
        if (nodeIndex > current)
        {
            double toNodeFt = GeoMath.DistanceNm(aircraft.Position, route.Segments[current].Edge.ToNode.Position) * FtPerNm;
            for (int i = current + 1; i < nodeIndex; i++)
            {
                toNodeFt += route.Segments[i].Edge.DistanceNm * FtPerNm;
            }

            return -toNodeFt;
        }

        double pastFt =
            (current < count) ? AlongEdgeNm(aircraft, route.Segments[current]) * FtPerNm : PastRouteEndFt(aircraft, route.Segments[count - 1]);
        for (int i = nodeIndex; i < current; i++)
        {
            pastFt += route.Segments[i].Edge.DistanceNm * FtPerNm;
        }

        return pastFt;
    }

    /// <summary>How far (ft) <paramref name="aircraft"/> is past <paramref name="last"/>'s end node along its chord, negative while short of it.</summary>
    private static double PastRouteEndFt(AircraftState aircraft, TaxiRouteSegment last)
    {
        LatLon from = last.Edge.FromNode.Position;
        LatLon to = last.Edge.ToNode.Position;
        return GeoMath.AlongTrackDistanceNm(aircraft.Position, to, new TrueHeading(GeoMath.BearingTo(from, to))) * FtPerNm;
    }

    /// <summary>
    /// The closest <paramref name="point"/> comes to any of <paramref name="track"/>'s edges, feet, following the pavement
    /// (<see cref="EdgeClearanceFt"/>).
    /// </summary>
    internal static double TrackClearanceFt(IReadOnlyList<DirectionalEdge> track, LatLon point) => track.Min(edge => EdgeClearanceFt(edge, point));

    /// <summary>
    /// The room a held aircraft leaves the target's <see cref="Track"/> through the junction: its centre at least
    /// <see cref="CentreRequiredFt"/> off it (both half-spans plus the wingtip buffer), and its nose — the centre projected
    /// <see cref="HalfLengthFt"/> ahead, as <see cref="FollowingPhase"/> projects a nose — at least
    /// <see cref="NoseRequiredFt"/> off it (the target's half-span plus the buffer).
    /// </summary>
    private readonly record struct GiveWayClearance(
        IReadOnlyList<DirectionalEdge> Track,
        double CentreRequiredFt,
        double NoseRequiredFt,
        double HalfLengthFt
    )
    {
        /// <summary>The farthest (ft) a centre can lie from the track with the centre or the nose inside the room.</summary>
        internal double ReachFt => Math.Max(CentreRequiredFt, NoseRequiredFt + HalfLengthFt);

        /// <summary>Whether an aircraft centred at <paramref name="centre"/>, heading <paramref name="headingDeg"/>, is inside the room.</summary>
        internal bool IsInside(LatLon centre, double headingDeg) =>
            (TrackClearanceFt(Track, centre) <= CentreRequiredFt)
            || (TrackClearanceFt(Track, GeoMath.ProjectPoint(centre, new TrueHeading(headingDeg), HalfLengthFt / FtPerNm)) <= NoseRequiredFt);
    }

    /// <summary>
    /// Half the wingspan (ft) of <paramref name="aircraftType"/>: the FAA database's when it carries both aircraft's spans
    /// (<paramref name="faaSpans"/>, as <see cref="RequiredLateralClearanceFt"/> requires), the category span of
    /// <see cref="AircraftFootprint.ResolveWingspanFt"/> otherwise.
    /// </summary>
    private static double HalfSpanFt(string aircraftType, bool faaSpans) =>
        (faaSpans ? FaaAircraftDatabase.Get(aircraftType)!.WingspanFt!.Value : AircraftFootprint.ResolveWingspanFt(aircraftType)) / 2;

    /// <summary>
    /// How far (ft) <paramref name="aircraft"/>'s centre taxis along <paramref name="route"/> before its centre or its nose
    /// first comes inside <paramref name="clearance"/>: the current segment from the aircraft straight to its end node, then
    /// each later edge along its pavement, searched no further than
    /// <paramref name="nodeId"/>, where the track itself starts, nor beyond <paramref name="lookaheadFt"/> (the convergence
    /// look-ahead for a FOLLOWG merge, infinity for a GIVEWAY). The nose points along the aircraft's heading where it stands
    /// and along each piece of the route ahead. Zero when the aircraft is already inside; null when the walk ends on the
    /// look-ahead, or the route's end, short of the node.
    /// </summary>
    private static double? DistanceToTrackClearanceFt(
        AircraftState aircraft,
        TaxiRoute route,
        int nodeId,
        GiveWayClearance clearance,
        double lookaheadFt
    )
    {
        LatLon previous = aircraft.Position;
        if (clearance.IsInside(previous, aircraft.TrueHeading.Degrees))
        {
            return 0.0;
        }

        int start = Math.Max(route.CurrentSegmentIndex, 0);
        double walkedFt = 0;
        for (int i = start; (i < route.Segments.Count) && (walkedFt <= lookaheadFt); i++)
        {
            DirectionalEdge edge = route.Segments[i].Edge;
            foreach (LatLon next in i == start ? [edge.ToNode.Position] : PavementPoints(edge))
            {
                double pieceFt = GeoMath.DistanceNm(previous, next) * FtPerNm;
                if (CanComeInside(previous, pieceFt, clearance) && (FirstInsideClearanceFt(previous, next, pieceFt, clearance) is { } alongFt))
                {
                    return walkedFt + alongFt;
                }

                walkedFt += pieceFt;
                previous = next;
            }

            if (route.Segments[i].ToNodeId == nodeId)
            {
                return walkedFt;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an aircraft on a straight piece of <paramref name="pieceFt"/> starting at <paramref name="from"/> can come inside
    /// <paramref name="clearance"/> anywhere along it: false when the start lies farther from the track than the piece's length
    /// plus <see cref="GiveWayClearance.ReachFt"/>, since no point of the piece, nor a nose half a length ahead of it, is then
    /// close enough. Lets the walk skip the far pieces of a long route without stepping them.
    /// </summary>
    private static bool CanComeInside(LatLon from, double pieceFt, GiveWayClearance clearance) =>
        TrackClearanceFt(clearance.Track, from) <= pieceFt + clearance.ReachFt;

    /// <summary>
    /// How far (ft) along the straight piece <paramref name="from"/> → <paramref name="to"/> an aircraft centred on it, nose
    /// along it, first comes inside <paramref name="clearance"/>: stepped every <see cref="GiveWayStopStepFt"/>, then the
    /// crossing step halved down to a tenth of a foot. Null when the whole piece stays clear.
    /// </summary>
    private static double? FirstInsideClearanceFt(LatLon from, LatLon to, double pieceFt, GiveWayClearance clearance)
    {
        double headingDeg = GeoMath.BearingTo(from, to);
        int steps = Math.Max(1, (int)Math.Ceiling(pieceFt / GiveWayStopStepFt));
        for (int k = 1; k <= steps; k++)
        {
            double hi = (double)k / steps;
            if (!clearance.IsInside(LatLon.Lerp(from, to, hi), headingDeg))
            {
                continue;
            }

            double lo = (double)(k - 1) / steps;
            while ((hi - lo) * pieceFt > GiveWayStopResolutionFt)
            {
                double mid = (lo + hi) / 2;
                if (!clearance.IsInside(LatLon.Lerp(from, to, mid), headingDeg))
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo * pieceFt;
        }

        return null;
    }

    /// <summary>
    /// An edge's pavement as points in the direction it is driven, its start node left out: a fillet arc sampled off its
    /// Bézier, a straight edge through its <see cref="GroundEdge.IntermediatePoints"/>.
    /// </summary>
    private static IEnumerable<LatLon> PavementPoints(DirectionalEdge edge)
    {
        if (edge.Edge is GroundArc arc)
        {
            CubicBezier curve = arc.ToBezier();
            bool forward = arc.Nodes[0].Id == edge.FromNodeId;
            return Enumerable
                .Range(1, ArcClearanceSamples)
                .Select(i => (double)i / ArcClearanceSamples)
                .Select(t => curve.Evaluate(forward ? t : 1.0 - t))
                .Select(p => new LatLon(p.Lat, p.Lon));
        }

        return edge.Edge is GroundEdge straight ? EdgeGeometry.PointsFrom(straight, edge.FromNode).Skip(1) : [edge.ToNode.Position];
    }

    internal static bool ShareUpcomingNode(AircraftState subject, AircraftState reference)
    {
        TaxiRoute? routeA = FollowingPhase.DrivenRouteOf(subject);
        TaxiRoute? routeB = FollowingPhase.DrivenRouteOf(reference);
        if (routeA is null || routeB is null)
        {
            return false;
        }

        return FindSharedUpcomingNode(routeA, routeB) is not null;
    }

    /// <summary>
    /// True when <paramref name="mover"/> could pass <paramref name="obstacle"/> with at least
    /// half-wingspans plus <see cref="GroundOutlineSweep.WingtipBufferFt"/> of lateral room, given the mover's
    /// current heading. Mirrors the wingspan-bypass geometry in <see cref="ComputeClosingLimit"/>
    /// (an obstacle abeam or behind the heading is never blocking). Used by
    /// <see cref="FlightPhysics.UpdateGiveWayResume"/>'s stalemate-bypass fallback.
    /// </summary>
    internal static bool HasWingspanLateralClearance(AircraftState mover, AircraftState obstacle) =>
        HasWingspanLateralClearance(mover, mover.TrueHeading.Degrees, obstacle);

    /// <summary>
    /// True when <paramref name="mover"/> could pass <paramref name="obstacle"/> with at least
    /// half-wingspans plus <see cref="GroundOutlineSweep.WingtipBufferFt"/> of lateral room while travelling along
    /// <paramref name="moverDirectionDeg"/> (an obstacle abeam or behind that direction is never
    /// blocking). The direction is explicit because a pusher moves tail-first: its motion runs along
    /// <c>Ground.PushbackTrueHeading</c>, the reciprocal of the nose heading.
    ///
    /// <para>The obstacle contributes half its wingspan whatever its orientation: a neighbour sitting
    /// perpendicular to the mover's track actually presents half its length instead (a B738's 64.8 ft
    /// half-length against the 58.8 ft half-span used here), a 6–15 ft understatement for common types
    /// that the 25 ft <see cref="GroundOutlineSweep.WingtipBufferFt"/> absorbs.</para>
    /// </summary>
    internal static bool HasWingspanLateralClearance(AircraftState mover, double moverDirectionDeg, AircraftState obstacle)
    {
        double bearing = GeoMath.BearingTo(mover.Position, obstacle.Position);
        double angleDiff = HeadingDifference(moverDirectionDeg, bearing);
        if (angleDiff >= 90)
        {
            return true;
        }

        if (RequiredLateralClearanceFt(mover, obstacle) is not { } requiredLateralFt)
        {
            return false;
        }

        double distFt = GeoMath.DistanceNm(mover.Position, obstacle.Position) * FtPerNm;
        double lateralFt = distFt * Math.Sin(angleDiff * Math.PI / 180.0);
        return lateralFt > requiredLateralFt;
    }

    /// <summary>
    /// True when the two aircraft are travelling on near-parallel tracks — within
    /// <see cref="ParallelTrackToleranceDeg"/> of parallel or of anti-parallel — that are far enough apart for
    /// them to pass: each aircraft's lateral offset from the other's track exceeds the pair's
    /// <see cref="RequiredLateralClearanceFt"/>. Traffic on the taxiway alongside is passing, not closing,
    /// however small the straight-line distance between the two gets, so neither the closing distance rule in
    /// <see cref="ComputeClosingLimit"/> nor the head-on rule in <see cref="ResolveHeadOn"/> applies to it.
    ///
    /// <para>Both offsets are measured because the tolerance lets the two tracks differ, so the offset of B from
    /// A's track and the offset of A from B's are not the same number. A crossing pair — more than the tolerance
    /// off parallel — is never covered here and keeps the distance rule.</para>
    ///
    /// <para>The offsets are measured twice: where the two are now, and where they will be at the end of the
    /// look-ahead, both against the same track line — the bearing the aircraft is projected along, chosen by
    /// <see cref="ProjectionBearingDeg"/>: its current taxi-route segment where that segment runs within
    /// <see cref="ParallelTrackToleranceDeg"/> of its travel direction (<paramref name="dirA"/>/<paramref name="dirB"/>, so
    /// a push projects backwards along the way its tail is going), the travel direction itself otherwise. The route
    /// segment is preferred because it is the line the aircraft will actually drive: a nose wandered a few degrees
    /// inside its lane would project off the lane and spend the pair's margin on the wander. Each aircraft runs its
    /// bearing for its own speed times the longer of the pair's stopping times (<see cref="StopTimeSeconds"/>), capped at
    /// <see cref="ParallelTrackLookAheadCapSeconds"/>: the moment by which a bypass granted now would have to be paid
    /// for out of the stopping margin. Both offsets are signed and have to keep to the same side of the other's track
    /// line as well as clear their requirements, so a pair that crosses a track line anywhere inside the look-ahead
    /// loses the bypass — at that crossing the offset is zero, whatever either end of the projection measures — and the
    /// distance rule takes over while there is still room to brake. Exactly parallel or anti-parallel tracks keep both
    /// their side and their offset under the projection, so the neighbouring-lane pass the bypass exists for is
    /// untouched.</para>
    /// </summary>
    private static bool HasParallelTrackLateralRoom(AircraftState a, double dirA, AircraftState b, double dirB)
    {
        double trackDiff = HeadingDifference(dirA, dirB);
        if ((trackDiff > ParallelTrackToleranceDeg) && (trackDiff < 180.0 - ParallelTrackToleranceDeg))
        {
            return false;
        }

        if ((RequiredLateralClearanceFt(a, b) is not { } requiredForA) || (RequiredLateralClearanceFt(b, a) is not { } requiredForB))
        {
            return false;
        }

        double bearingA = ProjectionBearingDeg(a, dirA);
        double bearingB = ProjectionBearingDeg(b, dirB);
        (double FromA, double FromB) current = LateralOffsetsFt(a.Position, bearingA, b.Position, bearingB);
        if ((Math.Abs(current.FromA) <= requiredForA) || (Math.Abs(current.FromB) <= requiredForB))
        {
            return false;
        }

        double horizonSeconds = Math.Min(Math.Max(StopTimeSeconds(a), StopTimeSeconds(b)), ParallelTrackLookAheadCapSeconds);
        LatLon projectedA = GeoMath.ProjectPointRaw(a.Position, bearingA, a.GroundSpeed * horizonSeconds / 3600.0);
        LatLon projectedB = GeoMath.ProjectPointRaw(b.Position, bearingB, b.GroundSpeed * horizonSeconds / 3600.0);

        (double FromA, double FromB) projected = LateralOffsetsFt(projectedA, bearingA, projectedB, bearingB);
        return ClearsTrackLine(current.FromA, projected.FromA, requiredForA) && ClearsTrackLine(current.FromB, projected.FromB, requiredForB);
    }

    /// <summary>
    /// The bearing to project <paramref name="ac"/> along, and to measure the other aircraft's signed offset against:
    /// the bearing of its current taxi-route segment — read as the chord from the segment's departure node to its
    /// arrival node, the way the route-lateral test reads an arc — when that segment runs within
    /// <see cref="ParallelTrackToleranceDeg"/> of its travel direction <paramref name="travelDirDeg"/>, otherwise the
    /// travel direction itself. No route, a route with no segment left to drive, and a segment set off the direction of
    /// travel (a pushback running against its own segment, a lane's fillet) all fall back to the travel direction.
    /// </summary>
    private static double ProjectionBearingDeg(AircraftState ac, double travelDirDeg)
    {
        if (FollowingPhase.DrivenRouteOf(ac)?.CurrentSegment is not { } segment)
        {
            return travelDirDeg;
        }

        double segmentBearing = GeoMath.BearingTo(segment.Edge.FromNode.Position, segment.Edge.ToNode.Position);
        return HeadingDifference(travelDirDeg, segmentBearing) > ParallelTrackToleranceDeg ? travelDirDeg : segmentBearing;
    }

    /// <summary>
    /// How long <paramref name="ac"/> takes to brake to a stop from its current ground speed at its category's taxi
    /// deceleration rate (<see cref="CategoryPerformance.TaxiDecelRate"/>), seconds: the window the parallel-track
    /// bypass has to stay good for.
    /// </summary>
    private static double StopTimeSeconds(AircraftState ac) =>
        ac.GroundSpeed / CategoryPerformance.TaxiDecelRate(AircraftCategorization.Categorize(ac.AircraftType));

    /// <summary>
    /// True when one aircraft's signed offset from the other's track line clears <paramref name="requiredFt"/> at both
    /// ends of the look-ahead — <paramref name="currentFt"/> where it is now, <paramref name="projectedFt"/> where the
    /// projection leaves it — and stays on the same side of that line. The offset is linear over a straight-line
    /// projection (the line belongs to the other aircraft, which runs along it), so equal signs at the two ends mean
    /// it never reaches zero in between and its smallest magnitude over the horizon is at one of the ends. A sign
    /// change is this pair crossing that track line inside the look-ahead, where the offset is zero: no room measured
    /// either side of the crossing is worth anything.
    /// </summary>
    private static bool ClearsTrackLine(double currentFt, double projectedFt, double requiredFt) =>
        (Math.Sign(currentFt) == Math.Sign(projectedFt)) && (Math.Abs(currentFt) > requiredFt) && (Math.Abs(projectedFt) > requiredFt);

    /// <summary>
    /// The signed lateral offset of each aircraft from the other's track line, feet, when the two sit at
    /// <paramref name="posA"/>/<paramref name="posB"/> and travel along <paramref name="dirA"/>/<paramref name="dirB"/>:
    /// positive to the right of that direction. One formula measures the offsets where they are now and where the
    /// projection leaves them, and a sign change between the two is the pair crossing that track line.
    /// </summary>
    private static (double FromA, double FromB) LateralOffsetsFt(LatLon posA, double dirA, LatLon posB, double dirB) =>
        (GeoMath.SignedCrossTrackDistanceNmRaw(posB, posA, dirA) * FtPerNm, GeoMath.SignedCrossTrackDistanceNmRaw(posA, posB, dirB) * FtPerNm);

    /// <summary>
    /// The side-by-side room two aircraft need to pass each other: half of each wingspan plus
    /// <see cref="GroundOutlineSweep.WingtipBufferFt"/>. Null when the FAA database carries no wingspan for either type, which
    /// every caller treats as "cannot show the pass is safe" rather than as a clearance.
    /// </summary>
    private static double? RequiredLateralClearanceFt(AircraftState mover, AircraftState obstacle)
    {
        double? moverWing = FaaAircraftDatabase.Get(mover.AircraftType)?.WingspanFt;
        double? obstacleWing = FaaAircraftDatabase.Get(obstacle.AircraftType)?.WingspanFt;
        if ((moverWing is not { } moverSpanFt) || (obstacleWing is not { } obstacleSpanFt))
        {
            return null;
        }

        return (moverSpanFt / 2) + (obstacleSpanFt / 2) + GroundOutlineSweep.WingtipBufferFt;
    }
}
