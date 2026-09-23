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
//     pin even when stopped (no self-pin crawl); if both would stop, the holder
//     is chosen by ChooseMutualStopHolder — the follower (other aircraft nearly
//     dead-ahead of it) holds while the lead proceeds, or a deterministic callsign
//     tie-break for near-symmetric geometry. Head-on fallback (both stop) applies
//     only to near-anti-parallel approaches (>= HeadOnMinHeadingDiffDeg); oblique
//     crossings use the holder arbitration.
//
// Hold classification: a routed aircraft with Ground.Hold set (HOLDPOSITION or
// GIVEWAY) classifies as Stationary. It won't move until the resume condition
// fires (FlightPhysics.UpdateGiveWayResume for GIVEWAY, operator RES for either),
// so other aircraft can pass laterally with wingspan clearance. The diagnostic
// log distinguishes the kind of hold so DebugSink consumers can tell whether the
// stop is intent-bearing ("Yielding to SWA123") or unconditional ("HoldPosition").
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
    private const double HeldStationarySpeedKts = 3.0;
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
    private const double SearchRangeNm = 0.3;

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
        Following,
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
        Action<string>? explicitLog = diagnosticLog;
        Action<string>? sink = DebugSink;
        if (sink is not null)
        {
            diagnosticLog = explicitLog is null
                ? sink
                : line =>
                {
                    explicitLog(line);
                    sink(line);
                };
        }

        for (int i = 0; i < aircraft.Count; i++)
        {
            aircraft[i].Ground.SpeedLimit = null;
            aircraft[i].Ground.AutoYieldTarget = null;
            aircraft[i].Ground.AutoYieldIsFollowing = false;

            if (aircraft[i].Ground.ConflictBreakRemainingSeconds > 0)
            {
                aircraft[i].Ground.ConflictBreakRemainingSeconds = Math.Max(0, aircraft[i].Ground.ConflictBreakRemainingSeconds - deltaSeconds);
            }
        }

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

            (MovementState state, double? dir) = Classify(ac);
            entries.Add((ac, state, dir));
            string holdReason = ac.Ground.Hold switch
            {
                { Kind: HoldKind.GiveWay, YieldTarget: { } t } => $" hold=GiveWay→{t}",
                { Kind: HoldKind.HoldPosition } => " hold=HoldPosition",
                _ => string.Empty,
            };
            diagnosticLog?.Invoke(
                $"[Classify] {ac.Callsign}: {state}{holdReason}, dir={dir?.ToString("F0") ?? "null"}, gs={ac.GroundSpeed:F1}, phase={ac.Phases?.CurrentPhase?.Name ?? "null"}, route={ac.Ground.AssignedTaxiRoute?.CurrentSegmentIndex.ToString() ?? "null"}/{ac.Ground.AssignedTaxiRoute?.Segments.Count.ToString() ?? "null"}"
            );
        }

        for (int i = 0; i < entries.Count; i++)
        {
            (AircraftState? a, MovementState stateA, double? dirA) = entries[i];

            if (stateA == MovementState.Following)
            {
                continue;
            }

            if (a.Ground.ConflictBreakRemainingSeconds > 0)
            {
                continue;
            }

            for (int j = i + 1; j < entries.Count; j++)
            {
                (AircraftState? b, MovementState stateB, double? dirB) = entries[j];

                if (stateB == MovementState.Following)
                {
                    continue;
                }

                if (b.Ground.ConflictBreakRemainingSeconds > 0)
                {
                    continue;
                }

                double distNm = GeoMath.DistanceNm(a.Position, b.Position);
                if (distNm > SearchRangeNm)
                {
                    continue;
                }

                double distFt = distNm * FtPerNm;
                PairKind kind = ClassifyPair(a, stateA, b, stateB, layout);

                // Make the controller-supplied GIVEWAY relationship visible. The pair
                // resolution is still Stationary-driven (one aircraft is held, so it
                // already classifies as Stationary), but the operator sees who is
                // yielding to whom rather than an anonymous "Stationary" pair.
                if (a.Ground.Hold is { } ha && ha.IsGiveWayFor(b.Callsign))
                {
                    diagnosticLog?.Invoke($"[Pair] ControllerGiveWay {a.Callsign}→{b.Callsign}");
                }
                else if (b.Ground.Hold is { } hb && hb.IsGiveWayFor(a.Callsign))
                {
                    diagnosticLog?.Invoke($"[Pair] ControllerGiveWay {b.Callsign}→{a.Callsign}");
                }

                diagnosticLog?.Invoke($"[Pair] {a.Callsign}({stateA})+{b.Callsign}({stateB}): dist={distFt:F0}ft → {kind}");

                switch (kind)
                {
                    case PairKind.Distant:
                    case PairKind.Stationary:
                        break;

                    case PairKind.Pushback:
                        // Pushback is its own world — the dedicated buffer logic is
                        // sufficient; we don't run closing/head-on on top.
                        ResolvePushbackYield(a, stateA, dirA, b, stateB, dirB, distFt, diagnosticLog);
                        break;

                    case PairKind.SameEdgeTrailing:
                        ResolveSameEdgeTrailing(a, b, distFt, layout!, diagnosticLog);
                        break;

                    case PairKind.SameEdgeHeadOn:
                        ResolveSameEdgeHeadOn(a, b, distFt, diagnosticLog);
                        break;

                    case PairKind.Converging:
                    {
                        AircraftState? convWinner = ResolveConvergence(a, b, layout!, diagnosticLog);
                        // Closing-proximity is the physical-overlap safety net, but at a true
                        // merge (both routes onto the shared node's lane) there is no lateral
                        // room for the wingspan bypass to open — applied symmetrically it pins
                        // BOTH aircraft to zero (a deadlock). When convergence has chosen a
                        // winner and both would stop, hold only the yielder so the winner (the
                        // merge-order leader, nearer the shared node) proceeds — mirroring
                        // ResolveCrossing's one-holds-one-goes. Head-on is skipped: convergence
                        // already made the pair-level decision.
                        ApplyConvergenceClosing(a, stateA, dirA, b, stateB, dirB, distFt, convWinner, diagnosticLog);
                        break;
                    }

                    case PairKind.Crossing:
                        ResolveCrossing(a, stateA, b, stateB, distFt, layout is not null, diagnosticLog);
                        break;
                }
            }
        }

        _tugRunContinuations = null;
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

        if (phaseName is not null && phaseName.StartsWith("Following", StringComparison.Ordinal))
        {
            return (MovementState.Following, null);
        }

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

        if (ac.Ground.AssignedTaxiRoute?.CurrentSegment is not null)
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
            TaxiRouteSegment? segA = a.Ground.AssignedTaxiRoute?.CurrentSegment;
            TaxiRouteSegment? segB = b.Ground.AssignedTaxiRoute?.CurrentSegment;
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

            TaxiRoute? routeA = a.Ground.AssignedTaxiRoute;
            TaxiRoute? routeB = b.Ground.AssignedTaxiRoute;
            if (routeA is not null && routeB is not null && FindSharedUpcomingNode(routeA, routeB) is not null)
            {
                return PairKind.Converging;
            }
        }

        return PairKind.Crossing;
    }

    // --- Conflict resolution ---

    private static void ResolveSameEdgeTrailing(
        AircraftState a,
        AircraftState b,
        double distFt,
        AirportGroundLayout layout,
        Action<string>? diagnosticLog
    )
    {
        TaxiRouteSegment segA = a.Ground.AssignedTaxiRoute!.CurrentSegment!;
        TaxiRouteSegment segB = b.Ground.AssignedTaxiRoute!.CurrentSegment!;

        double distAToTarget = DistToSegTarget(a, segA, layout);
        double distBToTarget = DistToSegTarget(b, segB, layout);

        diagnosticLog?.Invoke(
            $"  [SameEdgeTrailing] edge={segA.FromNodeId}→{segA.ToNodeId}: {a.Callsign} d2t={distAToTarget:F4}nm, {b.Callsign} d2t={distBToTarget:F4}nm"
        );

        if (distAToTarget > distBToTarget)
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
        TaxiRoute? routeA = a.Ground.AssignedTaxiRoute;
        TaxiRoute? routeB = b.Ground.AssignedTaxiRoute;
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

        ApplyMinLimit(holder, 0, "same-edge head-on hold", mover, distFt);
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
        TaxiRoute routeA = a.Ground.AssignedTaxiRoute!;
        TaxiRoute routeB = b.Ground.AssignedTaxiRoute!;

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
            double winnerLengthFt =
                FaaAircraftDatabase.Get(winner.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(winner.AircraftType);
            double winnerClearSec = (winnerDistFt + winnerLengthFt) / (winner.IndicatedAirspeed * FtPerNm / 3600.0);
            double yielderSpeedKts = Math.Max(yielder.IndicatedAirspeed, ConvergenceNominalTaxiSpeedKts);
            double yielderArriveSec = yielderDistFt / (yielderSpeedKts * FtPerNm / 3600.0);
            if (yielderArriveSec > winnerClearSec + ConvergenceClearanceBufferSec)
            {
                diagnosticLog?.Invoke(
                    $"  [Convergence] shared node={sharedNodeId.Value}: {winner.Callsign} clears in {winnerClearSec:F0}s, {yielder.Callsign} arrives in {yielderArriveSec:F0}s — no slowdown (clears first)"
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

        diagnosticLog?.Invoke(
            $"  [Convergence] shared node={sharedNodeId.Value}: {a.Callsign} {distAFt:F0}ft away, {b.Callsign} {distBFt:F0}ft away → {yielder.Callsign} yields to {winner.Callsign}, pairDist={conflictDistFt:F0}ft, limit={limitSpeed:F1}"
        );

        ApplyMinLimit(yielder, limitSpeed, "convergence", winner, conflictDistFt);
        yielder.Ground.AutoYieldTarget = winner.Callsign;
        return winner;
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

        (double Limit, string Reason)? winnerLimit = winnerDir is { } wd
            ? ComputeClosingLimit(winner, wd, yielder, yielderState, distFt, diagnosticLog)
            : null;
        (double Limit, string Reason)? yielderLimit = yielderDir is { } yd
            ? ComputeClosingLimit(yielder, yd, winner, winnerState, distFt, diagnosticLog)
            : null;

        if (winnerLimit is { Limit: <= 0 } && yielderLimit is { Limit: <= 0 })
        {
            diagnosticLog?.Invoke($"  [Convergence] merge mutual-stop: {yielder.Callsign} holds, {winner.Callsign} proceeds");
            ApplyMinLimit(yielder, 0, "convergence merge hold", winner, distFt);
            return;
        }

        if (winnerLimit is { } rw)
        {
            ApplyMinLimit(winner, rw.Limit, rw.Reason, yielder, distFt);
        }
        if (yielderLimit is { } ry)
        {
            ApplyMinLimit(yielder, ry.Limit, ry.Reason, winner, distFt);
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
    /// through to the symmetric calls unchanged).
    /// </summary>
    private static void ResolvePushbackYield(
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
            return;
        }

        var partyA = new PushbackParty(a, stateA, dirA);
        var partyB = new PushbackParty(b, stateB, dirB);
        if (TryResolveGiveWayToPushback(partyA, partyB, distFt, diagnosticLog) || TryResolveGiveWayToPushback(partyB, partyA, distFt, diagnosticLog))
        {
            return;
        }

        ResolvePushbackSide(partyA, partyB, distFt, diagnosticLog);
        ResolvePushbackSide(partyB, partyA, distFt, diagnosticLog);
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

        if (IsOnRunway(mover.Aircraft) || (mover.Aircraft.Phases?.CurrentPhase is CrossingRunwayPhase))
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
    /// for one it would actually hit. Returns false (leaving the pair to the independent sides) when the two
    /// would not wedge.
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
    private static bool TryResolveGiveWayToPushback(PushbackParty pusher, PushbackParty mover, double distFt, Action<string>? diagnosticLog)
    {
        if ((pusher.Direction is not { } pushDir) || !WouldDeadlock(pusher, mover))
        {
            return false;
        }

        diagnosticLog?.Invoke($"    [Pushback] {mover.Aircraft.Callsign} gives way to pushback {pusher.Aircraft.Callsign}: dist={distFt:F0}ft");
        ApplyMinLimit(mover.Aircraft, 0, "give way to pushback", pusher.Aircraft, distFt);
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
                return true;
            }

            diagnosticLog?.Invoke(
                $"    [Pushback] {pusher.Aircraft.Callsign} push path passes {mover.Aircraft.Callsign} at "
                    + $"{pushPathClearanceFt:F0}ft < clear({requiredLateralFt:F0}ft)"
            );
        }

        ApplyClosingLimit(pusher.Aircraft, pushDir, mover.Aircraft, MovementState.Stationary, distFt, diagnosticLog);
        return true;
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
                ApplyMinLimit(pusher, closing.Limit, closing.Reason, other.Aircraft, distFt);
                ShowTugMoveYield(pusher, other.Aircraft, closing.Limit);
            }

            return;
        }

        if (!HasWingspanLateralClearance(pusher, pushDir, other.Aircraft))
        {
            ApplyMinLimit(pusher, 0, "pushback yield", other.Aircraft, distFt);
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
        if (dirA is not null)
        {
            ApplyClosingLimit(a, dirA.Value, b, stateB, distFt, diagnosticLog);
        }
        if (dirB is not null)
        {
            ApplyClosingLimit(b, dirB.Value, a, stateA, distFt, diagnosticLog);
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
    private static (double Limit, string Reason)? ComputeClosingLimit(
        AircraftState mover,
        double moveDir,
        AircraftState obstacle,
        MovementState obstacleState,
        double distFt,
        Action<string>? diagnosticLog
    )
    {
        double bearing = GeoMath.BearingTo(mover.Position, obstacle.Position);
        double angleDiff = HeadingDifference(moveDir, bearing);
        if (angleDiff >= 90)
        {
            diagnosticLog?.Invoke($"    [Closing] {mover.Callsign}→{obstacle.Callsign}: diff={angleDiff:F0}° ≥90, not closing");
            return null;
        }

        bool isStationary = obstacleState == MovementState.Stationary;
        bool stationaryGate = !WingspanLateralCheckRequireStationary || isStationary;
        if (TugMoveAgainstParked(mover, obstacle) is { } tugMove)
        {
            return TugMoveLimit(mover, tugMove, obstacle, distFt, diagnosticLog);
        }

        // A moving obstacle on a neighbouring, near-parallel lane is passing rather than closing, so it takes
        // the lateral bypass too: it is as safe to pass as a parked one, and without this the straight-line
        // distance rule crawls or stops an aircraft for traffic on the taxiway alongside it.
        double obstacleDir = obstacle.Ground.PushbackTrueHeading?.Degrees ?? obstacle.TrueHeading.Degrees;
        bool lateralGate = stationaryGate || (!isStationary && HasParallelTrackLateralRoom(mover, moveDir, obstacle, obstacleDir));

        if (WingspanLateralCheckEnabled && lateralGate && (RequiredLateralClearanceFt(mover, obstacle) is { } requiredLateralFt))
        {
            double lateralFt = distFt * Math.Sin(angleDiff * Math.PI / 180.0);
            if (lateralFt > requiredLateralFt)
            {
                diagnosticLog?.Invoke(
                    $"    [Closing] {mover.Callsign}→{obstacle.Callsign}: lateral={lateralFt:F0}ft > clearance({requiredLateralFt:F0}ft), can pass"
                );
                return null;
            }

            // A live-traffic shadow standing still is as fixed as a parked aircraft — it is external, so
            // nothing we do moves it — and so is just as safe to route past.
            bool staysPut = IsParkedOrHeld(obstacle) || (obstacle.IsShadow && (obstacleState == MovementState.Stationary));
            if (staysPut && (RouteLateralClearanceFt(mover, obstacle, distFt) is { } routeLateralFt) && (routeLateralFt > requiredLateralFt))
            {
                diagnosticLog?.Invoke(
                    $"    [Closing] {mover.Callsign}→{obstacle.Callsign}: route lateral={routeLateralFt:F0}ft ≥ clear({requiredLateralFt:F0}ft), can pass"
                );
                return null;
            }
        }

        if (IsOnRunway(mover) && !IsOnRunway(obstacle) && obstacle.GroundSpeed <= 0)
        {
            diagnosticLog?.Invoke($"    [Closing] {mover.Callsign}→{obstacle.Callsign}: mover on runway, obstacle off-runway, skip");
            return null;
        }

        (double stopDist, double trailDist) = GetSeparation(obstacle, mover);
        if (distFt <= stopDist)
        {
            diagnosticLog?.Invoke($"    [Closing] {mover.Callsign}→{obstacle.Callsign}: {distFt:F0}ft ≤ stop({stopDist:F0}ft) → limit=0");
            return (0, "proximity stop");
        }

        if (distFt <= trailDist)
        {
            double limitSpeed = Math.Max(obstacle.GroundSpeed, SlowTaxiSpeedKts);
            diagnosticLog?.Invoke(
                $"    [Closing] {mover.Callsign}→{obstacle.Callsign}: {distFt:F0}ft ≤ trail({trailDist:F0}ft) → limit={limitSpeed:F1}"
            );
            return (limitSpeed, "proximity trail");
        }

        return null;
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
        if (mover.Ground.AssignedTaxiRoute is not { } route)
        {
            return null;
        }

        int start = Math.Max(route.CurrentSegmentIndex, 0);
        double boundFt =
            (distFt * RouteClearanceBoundFactor)
            + (FaaAircraftDatabase.Get(obstacle.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(obstacle.AircraftType));
        double? closestFt = null;
        double walkedFt = 0;
        for (int i = start; (i < route.Segments.Count) && (walkedFt < boundFt); i++)
        {
            DirectionalEdge edge = route.Segments[i].Edge;
            double segmentFt = EdgeClearanceFt(edge, obstacle.Position);
            closestFt = closestFt is { } best ? Math.Min(best, segmentFt) : segmentFt;
            walkedFt += i == start ? GeoMath.DistanceNm(mover.Position, edge.ToNode.Position) * FtPerNm : edge.DistanceNm * FtPerNm;
        }

        return closestFt;
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
    private static (double Limit, string Reason)? TugMoveLimit(
        AircraftState mover,
        PushbackPhase tugMove,
        AircraftState obstacle,
        double distFt,
        Action<string>? diagnosticLog
    )
    {
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

        double limitKts = TugMoveBrakingLimitKts(mover, failAlongFt);
        diagnosticLog?.Invoke(
            $"    [Closing] {mover.Callsign}→{obstacle.Callsign}: tug move fouls {failAlongFt:F1}ft along ≤ margin({marginFt:F1}ft) → "
                + $"limit={limitKts:F2}kt"
        );
        return (limitKts, "outline stop");
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
    /// Where a tug move fouls a parked or held neighbour: its <see cref="GroundOutline"/>, swept along the rest of its
    /// move (<see cref="PushbackPhase.RemainingPath"/>), may not come within <see cref="GroundOutlineSweep.WingtipBufferFt"/> of the
    /// neighbour's — or, for a neighbour the move already started closer to than that, as the aircraft on the next stand
    /// usually is, no closer than it was when the move began, less <see cref="GroundOutlineSweep.OutlineClearanceSlackFt"/>. Null when the
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
        IReadOnlyList<(TugPose Pose, double AlongFt)> path = tugMove.RemainingPath(mover, TugRunContinuation(mover));
        var frame = new GroundOutlineFrame(mover.Position);
        GroundOutlineSweepResult swept = GroundOutlineSweep.Sweep(
            path,
            tugMove.StartPose(mover),
            frame,
            GroundOutlineSize.Of(mover.AircraftType, towedNoseFirst: tugMove.Kind == PushbackLegKind.Pull),
            obstacle.Position,
            obstacle.TrueHeading.Degrees,
            GroundOutlineSize.Of(obstacle.AircraftType, towedNoseFirst: false)
        );
        if (swept.Foul is not { } foul)
        {
            string closestText = swept.ClosestFt is { } closestFt ? $"{closestFt:F1}ft" : "nothing in reach";
            diagnosticLog?.Invoke(
                $"    [Outline] {mover.Callsign}→{obstacle.Callsign}: {tugMove.Kind} started {swept.StartClearanceFt:F1}ft off, closest "
                    + $"{closestText} over {swept.SampleCount} samples ≥ floor({swept.FloorFt:F1}ft), passable"
            );
            return null;
        }

        diagnosticLog?.Invoke(
            $"    [Outline] {mover.Callsign}→{obstacle.Callsign}: {tugMove.Kind} started {swept.StartClearanceFt:F1}ft off, sample "
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
    /// </summary>
    public static void ShowTugMoveYield(AircraftState mover, AircraftState obstacle, double limitKts)
    {
        if (TugMoveAgainstParked(mover, obstacle) is not { } tugMove)
        {
            return;
        }

        if (limitKts >= tugMove.CommandedSpeedKts(mover))
        {
            return;
        }

        mover.Ground.AutoYieldTarget = obstacle.Callsign;
        mover.Ground.AutoYieldIsFollowing = false;
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
            ApplyMinLimit(mover, result.Limit, result.Reason, obstacle, distFt);
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
    /// pick ONE deterministic holder (callsign) and let the other proceed, instead of
    /// stopping both into a slow-motion gridlock.</item>
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

        // Rule 1: an aircraft on the runway surface proceeds; the other yields.
        if (IsOnRunway(a) != IsOnRunway(b))
        {
            AircraftState onRunway = IsOnRunway(a) ? a : b;
            double? onRunwayDir = IsOnRunway(a) ? closeDirA : closeDirB;
            MovementState onRunwayState = IsOnRunway(a) ? stateA : stateB;
            AircraftState yielder = IsOnRunway(a) ? b : a;
            double? yielderDir = IsOnRunway(a) ? closeDirB : closeDirA;
            MovementState yielderState = IsOnRunway(a) ? stateB : stateA;

            // Only override when the yielder can give way (a mover); a genuinely
            // parked obstacle keeps the normal closing/lateral treatment so the
            // runway aircraft still stops rather than driving through it.
            if (yielderDir is { } yd2)
            {
                bool conflict =
                    (onRunwayDir is { } od && ComputeClosingLimit(onRunway, od, yielder, yielderState, distFt, null) is not null)
                    || ComputeClosingLimit(yielder, yd2, onRunway, onRunwayState, distFt, null) is not null;
                if (conflict)
                {
                    diagnosticLog?.Invoke($"  [Crossing] {onRunway.Callsign} on runway → {yielder.Callsign} yields");
                    ApplyMinLimit(yielder, 0, "yield to runway aircraft", onRunway, distFt);
                }

                return;
            }
        }

        (double Limit, string Reason)? limitForA = closeDirA is { } da ? ComputeClosingLimit(a, da, b, stateB, distFt, diagnosticLog) : null;
        (double Limit, string Reason)? limitForB = closeDirB is { } db ? ComputeClosingLimit(b, db, a, stateA, distFt, diagnosticLog) : null;

        diagnosticLog?.Invoke(
            $"  [Crossing] {a.Callsign}(dir={closeDirA?.ToString("F0") ?? "none"},gs={a.GroundSpeed:F1})→limit={limitForA?.Limit.ToString("F1") ?? "null"} "
                + $"{b.Callsign}(dir={closeDirB?.ToString("F0") ?? "none"},gs={b.GroundSpeed:F1})→limit={limitForB?.Limit.ToString("F1") ?? "null"} dist={distFt:F0}ft"
        );

        if (limitForA is { Limit: <= 0 } && limitForB is { Limit: <= 0 })
        {
            // Crossing collision course: both would stop. Hold one so the other proceeds instead of
            // a mutual deadlock. When one aircraft is the clear follower (the other dead-ahead of
            // it), hold the follower and let the lead go — never release a follower through the
            // aircraft it is trailing; symmetric geometry falls back to a deterministic callsign
            // tie-break. closeDirA/closeDirB are non-null here (a <= 0 limit was computed from each).
            AircraftState holder = ChooseMutualStopHolder(a, closeDirA!.Value, b, closeDirB!.Value);
            AircraftState mover = ReferenceEquals(holder, a) ? b : a;
            diagnosticLog?.Invoke($"  [Crossing] mutual stop: {holder.Callsign} holds, {mover.Callsign} proceeds");
            ApplyMinLimit(holder, 0, "crossing hold", mover, distFt);
        }
        else
        {
            if (limitForA is { } resultA)
            {
                diagnosticLog?.Invoke($"  [Crossing] one-sided: {a.Callsign} limited {resultA.Limit:F1} ({resultA.Reason}) for {b.Callsign}");
                ApplyMinLimit(a, resultA.Limit, resultA.Reason, b, distFt);
                ShowTugMoveYield(a, b, resultA.Limit);
            }
            if (limitForB is { } resultB)
            {
                diagnosticLog?.Invoke($"  [Crossing] one-sided: {b.Callsign} limited {resultB.Limit:F1} ({resultB.Reason}) for {a.Callsign}");
                ApplyMinLimit(b, resultB.Limit, resultB.Reason, a, distFt);
                ShowTugMoveYield(b, a, resultB.Limit);
            }
        }

        // Head-on fallback: two aircraft actually moving toward each other.
        if (closeDirA is { } da3 && closeDirB is { } db3 && a.GroundSpeed > 0 && b.GroundSpeed > 0)
        {
            ResolveHeadOn(a, da3, b, db3, distFt, arbitrate: routesKnown);
        }
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
            // other proceed (follower-aware, callsign fallback for the near-symmetric anti-parallel
            // case); its closing-proximity limit still fires if they actually close.
            AircraftState holder = ChooseMutualStopHolder(a, dirA, b, dirB);
            AircraftState mover = ReferenceEquals(holder, a) ? b : a;
            ApplyMinLimit(holder, 0, "head-on hold", mover, distFt);
            return;
        }

        // No ground graph to confirm diverging routes (e.g. off-graph): treat as a genuine collision
        // course and stop both.
        ApplyMinLimit(a, 0, "head-on", b, distFt);
        ApplyMinLimit(b, 0, "head-on", a, distFt);
    }

    // --- Helpers ---

    /// <summary>
    /// On the runway by phase evidence alone (no runway geometry is available here): landing, taking
    /// off, lining up, or rolling out on the centerline. A generic holding-in-position aircraft is not
    /// on the runway for priority purposes.
    /// </summary>
    private static bool IsOnRunway(AircraftState ac) =>
        ac.IsShadow ? ac.Ground.ExternalOnRunway : RunwayOccupancy.OccupiesSurface(RunwayOccupancy.ClassifyByPhase(ac, runway: null));

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
        double leaderLength = FaaAircraftDatabase.Get(leader.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(leader.AircraftType);
        double trailerLength =
            FaaAircraftDatabase.Get(trailer.AircraftType)?.LengthFt ?? HoldShortAnnotator.CwtFallbackLengthFt(trailer.AircraftType);
        double stopDist = Math.Max(DefaultStopDistanceFt, ((leaderLength + trailerLength) / 2) + StopBufferFt);
        double trailDist = Math.Max(DefaultTrailDistanceFt, stopDist + 100.0);
        return (stopDist, trailDist);
    }

    private static void ApplyTrailLimit(AircraftState trailer, AircraftState leader, double distFt)
    {
        (double stopDist, double trailDist) = GetSeparation(leader, trailer);
        double maxSpeed;
        string reason;
        if (distFt <= stopDist)
        {
            maxSpeed = 0;
            reason = "trail stop";
        }
        else if (distFt <= trailDist)
        {
            maxSpeed = leader.GroundSpeed;
            reason = "trail match";
        }
        else
        {
            return;
        }

        ApplyMinLimit(trailer, maxSpeed, reason, leader, distFt);
    }

    private static void ApplyMinLimit(
        AircraftState aircraft,
        double maxSpeed,
        string? reason = null,
        AircraftState? other = null,
        double? distFt = null
    )
    {
        if (aircraft.IsShadow)
        {
            return;
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
                reason ?? "proximity",
                other?.Callsign ?? "?",
                distFt ?? 0
            );
            DebugSink?.Invoke(
                $"    [ApplyMinLimit] {aircraft.Callsign}: limit={maxSpeed:F1} (was {existing?.ToString("F1") ?? "none"}) reason={reason ?? "proximity"} other={other?.Callsign ?? "?"} dist={distFt?.ToString("F0") ?? "?"}ft"
            );
        }
    }

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
    /// Picks which aircraft to HOLD when two same-priority movers would each stop for the other.
    /// If one has the other clearly more dead-ahead than vice versa (off-nose angles differ by at
    /// least <see cref="FollowerLeadOffNoseMarginDeg"/>), that aircraft is the follower and holds,
    /// letting the lead — which has the other more abeam and moves away as it proceeds — go first
    /// (auto FOLLOW/BEHIND, 7110.65 3-7-2.a). Otherwise the geometry is effectively symmetric and a
    /// deterministic callsign tie-break decides. <paramref name="dirA"/>/<paramref name="dirB"/> are
    /// the movement (closing) directions the caller already resolved. Deterministic; no oscillation.
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

        return string.CompareOrdinal(a.Callsign, b.Callsign) >= 0 ? a : b;
    }

    private static double DistToSegTarget(AircraftState ac, TaxiRouteSegment seg, AirportGroundLayout layout)
    {
        if (layout.Nodes.TryGetValue(seg.ToNodeId, out GroundNode? node))
        {
            return GeoMath.DistanceNm(ac.Position, node.Position);
        }

        return seg.Edge.DistanceNm;
    }

    internal static int? FindSharedUpcomingNode(TaxiRoute routeA, TaxiRoute routeB)
    {
        double lookaheadNm = ConvergenceLookaheadFt / FtPerNm;

        var nodesA = new Dictionary<int, int>();
        double cumulativeA = 0;
        for (int i = routeA.CurrentSegmentIndex; i < routeA.Segments.Count; i++)
        {
            TaxiRouteSegment seg = routeA.Segments[i];
            cumulativeA += seg.Edge.DistanceNm;
            if (cumulativeA > lookaheadNm)
            {
                break;
            }
            nodesA.TryAdd(seg.ToNodeId, seg.FromNodeId);
        }

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
    /// True when <paramref name="target"/> is taxiing and will reach the node where its route merges into
    /// <paramref name="held"/>'s before <paramref name="held"/> does, and both routes leave that node the same way: the
    /// target is committed to the merge ahead, so the aircraft giving way to it can start rolling and fall in behind it.
    /// Traffic coming the other way along the held route also meets it at a shared node, but leaves it toward the held
    /// aircraft, so it never qualifies. Distances are measured along the routes.
    /// </summary>
    internal static bool TargetReachesMergeFirst(AircraftState held, AircraftState target)
    {
        if (held.Ground.AssignedTaxiRoute is not { } heldRoute || target.Ground.AssignedTaxiRoute is not { } targetRoute)
        {
            return false;
        }

        if (target.GroundSpeed <= GiveWayConstants.StationarySpeedThresholdKts)
        {
            return false;
        }

        if (FindSharedUpcomingNode(heldRoute, targetRoute) is not { } mergeNodeId)
        {
            return false;
        }

        return (RouteToNode(target, targetRoute, mergeNodeId) is { } targetLeg)
            && targetLeg.OnFinalTaxiwayIntoNode
            && (RouteToNode(held, heldRoute, mergeNodeId) is { } heldLeg)
            && (targetLeg.NextNodeId is { } targetNext)
            && (targetNext == heldLeg.NextNodeId)
            && (targetLeg.DistanceFt < heldLeg.DistanceFt);
    }

    /// <summary>
    /// How far along <paramref name="route"/> <paramref name="aircraft"/> is from <paramref name="nodeId"/>, in feet; the
    /// node the route goes to next; and whether the aircraft is already on the taxiway that leads into the node (it
    /// changes taxiway, if at all, only through the junction fillet ending there). Null when the node is not within
    /// <see cref="ConvergenceLookaheadFt"/>.
    /// </summary>
    private static (double DistanceFt, int? NextNodeId, bool OnFinalTaxiwayIntoNode)? RouteToNode(AircraftState aircraft, TaxiRoute route, int nodeId)
    {
        int start = Math.Max(route.CurrentSegmentIndex, 0);
        string? currentTaxiway = start < route.Segments.Count ? route.Segments[start].TaxiwayName : null;
        bool sameTaxiway = true;
        double walkedFt = 0;
        for (int i = start; i < route.Segments.Count; i++)
        {
            TaxiRouteSegment segment = route.Segments[i];
            walkedFt +=
                i == start ? GeoMath.DistanceNm(aircraft.Position, segment.Edge.ToNode.Position) * FtPerNm : segment.Edge.DistanceNm * FtPerNm;
            if (segment.ToNodeId == nodeId)
            {
                bool entersThroughJunctionArc = segment.Edge.Edge is GroundArc;
                bool onFinal = sameTaxiway && (entersThroughJunctionArc || (segment.TaxiwayName == currentTaxiway));
                int? next = i + 1 < route.Segments.Count ? route.Segments[i + 1].ToNodeId : null;
                return (walkedFt, next, onFinal);
            }

            sameTaxiway &= segment.TaxiwayName == currentTaxiway;

            if (walkedFt > ConvergenceLookaheadFt)
            {
                return null;
            }
        }

        return null;
    }

    internal static bool ShareUpcomingNode(AircraftState subject, AircraftState reference)
    {
        TaxiRoute? routeA = subject.Ground.AssignedTaxiRoute;
        TaxiRoute? routeB = reference.Ground.AssignedTaxiRoute;
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
        if (ac.Ground.AssignedTaxiRoute?.CurrentSegment is not { } segment)
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
