using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// A tug moves the aircraft through one <see cref="TugMove"/> — one continuous push or pull that
/// <see cref="TugMovePlanner"/> planned. Every <c>PUSH</c> form and <c>PUSHM</c> install one of these per planned
/// move, followed by the phase the move ends in.
///
/// <para><b>Motion.</b> Each tick <see cref="TugKinematics"/> supplies the direction of travel, turned by at most
/// the distance the aircraft is about to move divided by the type's turn radius, so a stopped aircraft never
/// rotates. A push leads with the tail: <see cref="AircraftGroundOps.PushbackTrueHeading"/> is the direction of
/// travel for the whole move, dwell included, and the nose is its reciprocal. A pull leads with the nose: that
/// field stays null and the nose is the direction of travel. <see cref="AircraftGroundOps.TowbarTrueHeading"/>
/// carries the tug itself: the direction from the nose gear out along the towbar, taken from the nose-gear steer
/// angle each step implies (<see cref="SteerTowbar"/>), set as the move starts and dropped as it ends unless the
/// tow flows into the next move. <see cref="FlightPhysics"/> moves the aircraft.</para>
///
/// <para><b>Speed.</b> <see cref="CategoryPerformance.PushbackSpeed"/>, or
/// <see cref="CategoryPerformance.PushbackAlignSpeed"/> on the last stretch of a creep move; on a step that turns
/// by more than a quarter of the most it may, slowed so the outer wingtip keeps the same pace. The tug picks the
/// speed up at <see cref="CategoryPerformance.TugAccelRate"/> and sheds it at
/// <see cref="CategoryPerformance.TugDecelRate"/>, both published as
/// <see cref="ControlTargets.DesiredAccelRate"/> / <see cref="ControlTargets.DesiredDecelRate"/> for physics to
/// integrate, so nothing on a towbar starts or stops at the aircraft's own taxi rates. Every stop is a braking
/// curve onto its end speed: ahead of the next turn in the move onto the wingtip cap, and — on a move that ends at
/// a genuine stop, the plan's last move or the one before a reversal's dwell — onto
/// <see cref="FinalApproachKts"/> for the last <see cref="FinalApproachFt"/>, so the final step lands inside the
/// 1 ft stop tolerance before the move stops dead. A move that continues into the next one
/// (<see cref="ContinuesIntoNextMove"/>) takes no end-of-move curve: it holds its speed to the boundary and hands
/// it to the next move, so a plan is one continuous tow rather than a queue of standing starts.</para>
///
/// <para><b>Dwell.</b> A move that reverses the previous one (<see cref="TugMove.DwellBefore"/>) waits
/// <see cref="DwellSeconds"/> at a standstill before it starts, counted only while the aircraft is stopped.</para>
/// </summary>
public sealed class PushbackPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("PushbackPhase");

    /// <summary>
    /// How long a reversal waits stopped before the tug moves the other way, seconds. A judgement call: AC 00-65A
    /// §11.17 says towing "should not start and stop suddenly" and gives no duration.
    /// </summary>
    public const double DwellSeconds = 5.0;

    private const double LogIntervalSeconds = 3.0;

    /// <summary>Ground speed at or below which a dwelling aircraft counts as stopped, knots.</summary>
    private const double AtRestKts = 0.01;

    /// <summary>
    /// A step counts as turning when it turns the direction of travel by more than this fraction of the most a step
    /// that long may turn it (step length / turn radius); a judgement call. A straight never turns, and a line
    /// move holding its line turns by a sliver of that.
    /// </summary>
    private const double TurningStepFraction = 0.25;

    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Within this distance of the planned end the tug is down to <see cref="FinalApproachKts"/>.</summary>
    public const double FinalApproachFt = 10.0;

    /// <summary>The last-stretch speed: a quarter-second step at 1 kt is 0.42 ft, inside the 1 ft stop tolerance.</summary>
    public const double FinalApproachKts = 1.0;

    /// <summary>Feet per second per knot: how fast a knot is, for the braking curve's unit conversion.</summary>
    private const double FtPerSecPerKt = GeoMath.FeetPerNm / 3600.0;

    /// <summary>Fuselage length assumed by <see cref="HasRampPriority"/> for a type the FAA database does not carry.</summary>
    private const double DefaultFuselageLengthFt = 110.0;

    /// <summary>The farthest apart two <see cref="RemainingPath"/> samples lie, feet.</summary>
    private const double RemainingPathSpacingFt = 5.0;

    /// <summary>The most the nose turns between two <see cref="RemainingPath"/> samples, degrees.</summary>
    private const double RemainingPathTurnDeg = 5.0;

    /// <summary>How far the aircraft moves before <see cref="RemainingPath"/> simulates the move again, feet.</summary>
    private const double RemainingPathRebuildFt = 2.0;

    private TugMove _move = null!;
    private LatLon _plannedEnd;
    private TugMoveProgress _progress;
    private LatLon _lastPosition;
    private double _dwellElapsedSeconds;
    private bool _progressPending;
    private LatLon? _pendingPushedFrom;
    private double _timeSinceLastLog;

    // One cache per caller: the turn look-ahead asks for this move alone and the conflict detector for the continuing
    // run, on alternating sub-ticks, so a single cache would invalidate each one on the other's call and simulate both
    // chains every sub-tick. None of it is snapshotted.
    private readonly PathCache _movePathCache = new();
    private readonly PathCache _runPathCache = new();

    /// <summary>The move this phase flies.</summary>
    public required TugMove Move
    {
        get => _move;
        init => _move = value;
    }

    /// <summary>
    /// Where the planner's simulation ended this move. A snapshot written before tug moves existed may restore a
    /// provisional value that the first tick replaces with a simulation from the live pose.
    /// </summary>
    public required LatLon PlannedEnd
    {
        get => _plannedEnd;
        init => _plannedEnd = value;
    }

    /// <summary>This move is the push-off from a stand, the first move of a plan that started parked.</summary>
    public bool StartsAtStand { get; init; }

    /// <summary>
    /// The tug goes straight on into another move when this one completes: the plan has a next move and it does not
    /// dwell. False for the plan's last move and for one followed by a reversal, both of which end at a standstill —
    /// so the boundary between two moves of the same kind is flown through at speed, while a tug that is about to
    /// reverse still stops first (AC 00-65A §11.17).
    /// </summary>
    public required bool ContinuesIntoNextMove { get; init; }

    /// <summary>
    /// This move is flown through from the stand push-off with no reversal between it and the push-off: leg 1 of a
    /// push off a stand, after the push-off itself. False on the push-off (which has
    /// <see cref="StartsAtStand"/> instead), on every move the plan reverses into, and on every move of a tow that
    /// did not start on a stand. <see cref="HasRampPriority"/> is what reads it.
    /// </summary>
    public required bool ContinuesStandPushOff { get; init; }

    /// <summary>
    /// On the stand push-off of a single-goal <c>PUSH</c> only: what a mid-push facing change needs to re-plan the
    /// push (issue #167). Null on every other move.
    /// </summary>
    public TugAmendment? Amendment { get; init; }

    /// <summary>Push (tail-first) or pull (nose-first).</summary>
    public PushbackLegKind Kind => Move.Kind;

    /// <summary>
    /// Whether this move has moved the aircraft yet. A reversal still waiting out its dwell has not, so the aircraft's
    /// last motion is still the previous move's, of the other kind.
    /// </summary>
    public bool HasMoved => _progress.DistanceFt > 0.0;

    public override string Name => "Pushback";

    /// <summary>
    /// Whether a mid-push facing change may still re-plan this push: the phase is a stand push-off that carries an
    /// <see cref="Amendment"/> and has not finished, so no turn has begun.
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <returns>True when the push can be amended.</returns>
    public bool CanAmend(AircraftState aircraft) =>
        (Amendment is not null) && (Status != PhaseStatus.Completed) && !TugKinematics.IsComplete(PoseOf(aircraft), Move, _progress);

    /// <summary>
    /// Whether this move is the committed push off a stand, which outranks taxiing traffic in the alley. That is leg
    /// 1 of a push off a stand and nothing else: the push-off (<see cref="StartsAtStand"/>) once the aircraft has
    /// moved more than half its fuselage length from where the move began — recomputed from the recorded start each
    /// time, so a snapshot restore reproduces it — and the moves flown through from it before the first reversal
    /// (<see cref="ContinuesStandPushOff"/>).
    ///
    /// <para><see cref="GroundConflictDetector"/> uses this to decide whether a pushback outranks taxiing traffic. A
    /// push that has committed the alley is not worth holding — stopping it frees nothing, it blocks the lane for
    /// longer, and the tug crew faces the aircraft and cannot see behind the tail. One still on its stand can wait
    /// for the traffic to go by, which is what a ramp controller means by "hold your push, traffic in the alley",
    /// and a tow already repositioning — the second leg of a <c>PUSHM</c>, or the push half of a three-point turn
    /// after a reversal — is ordinary ramp traffic that yields like anything else. 7110.65 §3-7-2 NOTE 2 leaves
    /// movement on a nonmovement area to the pilot, the operator and airport management, so this is the ramp
    /// convention modelled rather than an ATC instruction.</para>
    ///
    /// <para>A pull never gets here with priority: it never classifies as a push to the detector.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <returns>True when the move is the committed push off a stand.</returns>
    public bool HasRampPriority(AircraftState aircraft)
    {
        if (!StartsAtStand)
        {
            return ContinuesStandPushOff;
        }

        LatLon start = _progress.Start;
        if ((start.Lat == 0.0) && (start.Lon == 0.0))
        {
            return false;
        }

        double halfFuselageFt = (Data.Faa.FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? DefaultFuselageLengthFt) / 2.0;
        return FeetBetween(start, aircraft.Position) > halfFuselageFt;
    }

    /// <summary>
    /// Where the move the tug is running ends: <see cref="PlannedEnd"/>. Returns false only once the move is
    /// complete.
    ///
    /// <para><see cref="GroundConflictDetector"/> measures traffic against the segment from the aircraft to
    /// this point, which is how a push whose remaining track stays clear of an aircraft holding for it is
    /// allowed to finish instead of stopping nose-to-nose with it.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <param name="end">The move's end point when it has not finished.</param>
    /// <returns>True when <paramref name="end"/> was set.</returns>
    public bool TryGetPushLegEnd(AircraftState aircraft, out LatLon end)
    {
        end = default;
        if ((Status == PhaseStatus.Completed) || TugKinematics.IsComplete(PoseOf(aircraft), Move, _progress))
        {
            return false;
        }

        end = PlannedEnd;
        return true;
    }

    /// <summary>
    /// The pose this move began at: where <see cref="OnStart"/> found the aircraft, with the nose it had there. It comes
    /// from the move's progress, so it survives a snapshot round trip with the rest of the leg.
    ///
    /// <para><see cref="GroundConflictDetector"/> measures the outline clearance a parked neighbour started with from
    /// this pose. The rule it enforces is that a move may not bring the pair closer than it found them, and a floor
    /// taken from the live pose instead would follow the mover down and ratchet it into contact a foot at a time.</para>
    ///
    /// <para>A phase that was never started has no recorded start (the same unset <c>(0, 0)</c>
    /// <see cref="HasRampPriority"/> guards against); it answers the live pose, so the floor is anchored to where the
    /// aircraft is rather than to the Gulf of Guinea.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <returns>The pose the move began at, or the live pose when the move recorded no start.</returns>
    public TugPose StartPose(AircraftState aircraft)
    {
        LatLon start = _progress.Start;
        return (start.Lat == 0.0) && (start.Lon == 0.0)
            ? PoseOf(aircraft)
            : new TugPose(start, TugKinematics.FlipForKind(_progress.StartTravelTrueDeg, Move.Kind));
    }

    /// <summary>
    /// Where the rest of this move, and the moves in <paramref name="continuation"/> the tug runs straight on into, take
    /// the aircraft: poses with how far along the remaining path each one sits — the live pose first at zero feet, then
    /// the moves simulated from it as one chain (this move a straight for the distance it still owes, any other shape as
    /// it is), no more than <see cref="RemainingPathSpacingFt"/> and <see cref="RemainingPathTurnDeg"/> apart, with the
    /// along-distance running on through every move. Only the live pose once this move is complete.
    ///
    /// <para><see cref="GroundConflictDetector"/> sweeps the aircraft's outline along this path to decide whether a
    /// parked or held neighbour is in the way, and the along-path distance at which it first fouls one is how far the
    /// mover has left before it has to be stopped. It passes the rest of the tug's run, so a neighbour the next move
    /// walks into is found while there is still room to brake for it rather than at the boundary, at speed. A caller
    /// that only cares about this move — the ease-off look-ahead — passes an empty continuation.</para>
    ///
    /// <para>Each of the two callers keeps its own cache — the look-ahead asks for this move alone every sub-tick while
    /// the detector asks for the continuing run, and one shared cache would rebuild both chains on every call. Within a
    /// cache the simulation is kept and redone only once the aircraft has moved more than
    /// <see cref="RemainingPathRebuildFt"/> from where it ran, or the continuation changed; in between, the samples
    /// already passed are dropped and the rest are charged the distance already covered. None of it is in the snapshot:
    /// a restored phase simulates on first use.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <param name="continuation">The moves the tug runs on into after this one, in order; empty for this move alone.</param>
    /// <returns>The remaining path, starting at the live pose at zero feet.</returns>
    public IReadOnlyList<(TugPose Pose, double AlongFt)> RemainingPath(AircraftState aircraft, IReadOnlyList<TugMove> continuation)
    {
        PathCache cache = continuation.Count == 0 ? _movePathCache : _runPathCache;
        TugPose pose = PoseOf(aircraft);
        bool sameContinuation = SameMoves(cache.Continuation, continuation);
        if ((cache.PathFromHere is { } cached) && (cache.PathFromHerePose == pose) && sameContinuation)
        {
            return cached;
        }

        cache.PathFromHerePose = pose;
        cache.Continuation = continuation;
        if ((Status == PhaseStatus.Completed) || TugKinematics.IsComplete(pose, Move, _progress))
        {
            IReadOnlyList<(TugPose Pose, double AlongFt)> here = [(pose, 0.0)];
            cache.PathFromHere = here;
            return here;
        }

        List<(TugPose Pose, double AlongFt)>? simulated = cache.SimulatedPath;
        double movedFt = simulated is null ? 0.0 : FeetBetween(cache.SimulatedFrom, pose.Position);
        if ((simulated is null) || (movedFt > RemainingPathRebuildFt) || !sameContinuation)
        {
            simulated = SimulateRemaining(aircraft.AircraftType, pose, continuation);
            cache.SimulatedPath = simulated;
            cache.SimulatedFrom = pose.Position;
            movedFt = 0.0;
        }

        var path = new List<(TugPose Pose, double AlongFt)>(simulated.Count) { (pose, 0.0) };
        foreach ((TugPose sample, double alongFt) in simulated)
        {
            if (alongFt > movedFt)
            {
                path.Add((sample, alongFt - movedFt));
            }
        }

        cache.PathFromHere = path;
        return path;
    }

    /// <summary>
    /// One caller's <see cref="RemainingPath"/> working set: the chain as last simulated and where it was simulated
    /// from, and the path last handed out with the pose and the continuation it was built for.
    /// </summary>
    private sealed class PathCache
    {
        public List<(TugPose Pose, double AlongFt)>? SimulatedPath { get; set; }

        public LatLon SimulatedFrom { get; set; }

        public IReadOnlyList<(TugPose Pose, double AlongFt)>? PathFromHere { get; set; }

        public TugPose PathFromHerePose { get; set; }

        public IReadOnlyList<TugMove>? Continuation { get; set; }

        /// <summary>Drops the simulation and the path built from it, so the next call simulates from the live pose.</summary>
        public void Invalidate()
        {
            SimulatedPath = null;
            PathFromHere = null;
        }
    }

    /// <summary>
    /// The rest of this move and then <paramref name="continuation"/> flown from <paramref name="pose"/> as one chain,
    /// with each sample's distance along the whole path; the simulation's 5 ft samples are subdivided wherever the nose
    /// turns more than <see cref="RemainingPathTurnDeg"/> between two of them (over 5 ft the chord and the arc differ by
    /// well under a tenth of a foot). Every move after the first opens on the previous one's end pose, which is dropped
    /// so the chain carries each pose once.
    /// </summary>
    private List<(TugPose Pose, double AlongFt)> SimulateRemaining(string aircraftType, TugPose pose, IReadOnlyList<TugMove> continuation)
    {
        TugMove move =
            Move.Shape == TugMoveShape.Straight
                ? Move with
                {
                    StraightDistanceFt = Math.Max(0.0, Move.StraightDistanceFt - _progress.DistanceFt),
                }
                : Move;
        IReadOnlyList<TugMoveTrace> traces = TugKinematics.Simulate(pose, [move, .. continuation], aircraftType, TugMovePlanner.StepFt).Moves;
        var samples = new List<TugPose>();
        foreach (TugMoveTrace trace in traces)
        {
            for (int i = samples.Count == 0 ? 0 : 1; i < trace.Samples.Count; i++)
            {
                samples.Add(trace.Samples[i]);
            }
        }

        var path = new List<(TugPose Pose, double AlongFt)>(samples.Count * 2) { (samples[0], 0.0) };
        double alongFt = 0.0;
        for (int i = 1; i < samples.Count; i++)
        {
            TugPose from = samples[i - 1];
            TugPose to = samples[i];
            double stepFt = FeetBetween(from.Position, to.Position);
            double turnDeg = new TrueHeading(from.NoseTrueDeg).SignedAngleTo(new TrueHeading(to.NoseTrueDeg));
            int pieces = Math.Max(1, (int)Math.Ceiling(Math.Max(stepFt / RemainingPathSpacingFt, Math.Abs(turnDeg) / RemainingPathTurnDeg)));
            for (int piece = 1; piece <= pieces; piece++)
            {
                double fraction = (double)piece / pieces;
                var position = new LatLon(
                    from.Position.Lat + (fraction * (to.Position.Lat - from.Position.Lat)),
                    from.Position.Lon + (fraction * (to.Position.Lon - from.Position.Lon))
                );
                path.Add((new TugPose(position, from.NoseTrueDeg + (fraction * turnDeg)), alongFt + (fraction * stepFt)));
            }

            alongFt += stepFt;
        }

        return path;
    }

    /// <summary>
    /// Whether two continuations are the same moves in the same order, by identity: the planner mints one
    /// <see cref="TugMove"/> per move and every caller hands out those, so a re-planned run is a different list even
    /// where it flies the same shapes. An identical-but-rebuilt list only costs one more simulation.
    /// </summary>
    private static bool SameMoves(IReadOnlyList<TugMove>? a, IReadOnlyList<TugMove> b)
    {
        if ((a is null) || (a.Count != b.Count))
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override void OnStart(PhaseContext ctx)
    {
        AircraftState aircraft = ctx.Aircraft;
        TugPose pose = PoseOf(aircraft);
        _progress = TugMoveProgress.Begin(pose, Move);
        _lastPosition = pose.Position;
        ctx.Targets.TargetTrueHeading = null;
        aircraft.IsOnGround = true;
        aircraft.Ground.PushbackTrueHeading = Move.Kind == PushbackLegKind.Push ? new TrueHeading(pose.TravelTrueDeg(Move.Kind)) : null;

        // The tug hitches up on the fuselage axis. A move flown through from the one before finds the towbar already
        // set and keeps it, so a continuous tow does not snap the tug straight at the boundary.
        aircraft.Ground.TowbarTrueHeading ??= aircraft.TrueHeading;
        PublishTugRates(ctx);
        ctx.Targets.TargetSpeed = Move.DwellBefore ? 0 : CategoryPerformance.PushbackSpeed(ctx.Category);

        Log.LogDebug(
            "[Push] {Callsign}: started {Kind} {Shape} (tight={Tight}, creep={Creep}, dwell={Dwell}, stand={Stand}), nose={Nose:F1}, "
                + "plannedEnd=({EndLat:F6},{EndLon:F6}), pos=({Lat:F6},{Lon:F6})",
            aircraft.Callsign,
            Move.Kind,
            Move.Shape,
            Move.Tight,
            Move.Creep,
            Move.DwellBefore,
            StartsAtStand,
            pose.NoseTrueDeg,
            PlannedEnd.Lat,
            PlannedEnd.Lon,
            pose.Position.Lat,
            pose.Position.Lon
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        AircraftState aircraft = ctx.Aircraft;
        TugPose pose = PoseOf(aircraft);
        if (_progressPending)
        {
            ResolvePending(aircraft, pose);
        }

        _progress = TugKinematics.Record(_progress, pose, Move, FeetBetween(_lastPosition, pose.Position));
        _lastPosition = pose.Position;

        // Completion is judged before the hold: the feet a braking tow covers still belong to the move, so a hold
        // that lands inside the braking distance can carry the move through its end, and a finished move hands over
        // then rather than sitting on a covered distance until the hold is lifted.
        if (TugKinematics.IsComplete(pose, Move, _progress))
        {
            HandOver(ctx, pose);
            Log.LogDebug(
                "[Push] {Callsign}: {Kind} {Shape} complete after {DistanceFt:F1} ft, {OffEndFt:F2} ft off the planned end, nose={Nose:F1}",
                aircraft.Callsign,
                Move.Kind,
                Move.Shape,
                _progress.DistanceFt,
                FeetBetween(pose.Position, PlannedEnd),
                pose.NoseTrueDeg
            );
            return true;
        }

        // A controller hold brakes the tug rather than freezing it: physics sheds the speed at the towbar rate along
        // the headings the last driven tick left set, and the feet that takes are recorded above as the move's own.
        // Nothing else runs while the hold is in force.
        if (aircraft.Ground.IsImmobile)
        {
            PublishTugRates(ctx);
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        if (IsDwelling(ctx))
        {
            return false;
        }

        Drive(ctx, pose);
        TraceProgress(ctx);
        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        Log.LogDebug(
            "[Push] {Callsign}: OnEnd ({Status}), moved {DistanceFt:F1} ft, hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            endStatus,
            _progress.DistanceFt,
            ctx.Aircraft.TrueHeading.Degrees
        );

        // Only a move that ran to completion hands its speed to the move behind it. Every other end is the tug
        // letting go mid-move — a TAXI, TAXIAUTO, AIRTAXI, LAND, DEL or PUSHM clearing the phase, or an abort —
        // and the aircraft is still tail-first with a direction of travel about to flip, so it stops dead.
        if (endStatus == PhaseStatus.Completed)
        {
            HandOver(ctx, PoseOf(ctx.Aircraft));
        }
        else
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
        }

        ctx.Aircraft.Ground.PushbackTrueHeading = null;

        // The tug lets go with the phase, except where the tow goes straight on into the next move: that move's start
        // picks the towbar up where this one left it, so a flown-through boundary does not snap the tug straight. A
        // move the plan reverses after ends at a standstill, and the next move hitches up on the axis over its dwell.
        if (!((endStatus == PhaseStatus.Completed) && ContinuesIntoNextMove))
        {
            ctx.Aircraft.Ground.TowbarTrueHeading = null;
        }

        // The towbar rates belong to the tug, not to the aircraft: whatever comes next accelerates and brakes on
        // its own terms.
        ctx.Targets.DesiredAccelRate = null;
        ctx.Targets.DesiredDecelRate = null;
    }

    /// <summary>
    /// Publishes the rates physics integrates the tow at — <see cref="CategoryPerformance.TugAccelRate"/> and
    /// <see cref="CategoryPerformance.TugDecelRate"/>. Re-published every tick, the way the ground phases publish
    /// their targets, so nothing a command or another phase left behind survives into a tug move.
    /// </summary>
    /// <param name="ctx">The phase context.</param>
    private static void PublishTugRates(PhaseContext ctx)
    {
        ctx.Targets.DesiredAccelRate = CategoryPerformance.TugAccelRate(ctx.Category);
        ctx.Targets.DesiredDecelRate = CategoryPerformance.TugDecelRate(ctx.Category);
    }

    /// <summary>
    /// Hands the aircraft on when a move <em>completes</em>. One that continues into the next move keeps the speed it
    /// is carrying and publishes the move's speed as the target, so physics holds the tow at pace over the boundary
    /// until the next move's <see cref="OnStart"/> takes the targets over in the same tick. One that ends at a
    /// genuine stop — the plan's last, or the one before a reversal's dwell — stops the aircraft dead. A move ended
    /// any other way never gets here: <see cref="OnEnd"/> stops a cleared or aborted move itself.
    /// </summary>
    /// <param name="ctx">The phase context.</param>
    /// <param name="pose">The pose the move ended at.</param>
    private void HandOver(PhaseContext ctx, TugPose pose)
    {
        if (!ContinuesIntoNextMove)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
            return;
        }

        double radiusFt = TugKinematics.TurnRadiusFt(ctx.Aircraft.AircraftType, Move.Tight);
        ctx.Targets.TargetSpeed = MoveSpeedKts(ctx, pose, radiusFt, turning: false);
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.Pushback => CommandAcceptance.Allowed,
            // Redirecting an attached tug is ordinary: the new move replaces this leg and every leg still queued
            // behind it, the same way a taxi clearance mid-move takes the whole move with it.
            CanonicalCommandType.PushbackMulti => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is being pushed back; only HOLD/RES are accepted until pushback completes"),
        };
    }

    /// <summary>
    /// Waits out a reversal: holds the aircraft stopped and counts the time it has been stopped. False once the
    /// dwell is over, or when the move has none.
    /// </summary>
    private bool IsDwelling(PhaseContext ctx)
    {
        if (!Move.DwellBefore || (_dwellElapsedSeconds >= DwellSeconds))
        {
            return false;
        }

        PublishTugRates(ctx);
        ctx.Targets.TargetSpeed = 0;
        if (ctx.Aircraft.GroundSpeed <= AtRestKts)
        {
            _dwellElapsedSeconds += ctx.DeltaSeconds;
        }

        return true;
    }

    /// <summary>
    /// Sets this tick's speed and steers the direction of travel by the distance physics is about to move the
    /// aircraft. That distance is taken at the slower of the current speed and the new target: physics moves the
    /// speed toward the target before it moves the aircraft, so the aircraft covers at least that much and never
    /// turns tighter than the radius, speeding up or braking. The step is first steered at the uncapped speed; when
    /// it turns (<see cref="IsTurningStep"/>), the speed is capped for the wingtip and the step re-steered at it — a
    /// shorter step may turn less, but never by a smaller share of its maximum, so it still counts as turning.
    /// </summary>
    private void Drive(PhaseContext ctx, TugPose pose)
    {
        AircraftState aircraft = ctx.Aircraft;
        PublishTugRates(ctx);
        double radiusFt = TugKinematics.TurnRadiusFt(aircraft.AircraftType, Move.Tight);
        double speedKts = MoveSpeedKts(ctx, pose, radiusFt, turning: false);
        double stepFt = StepFt(ctx, speedKts);
        double travelDeg = TugKinematics.SteerTravel(pose, Move, _progress, radiusFt, stepFt);
        if (IsTurningStep(pose, travelDeg, stepFt, radiusFt))
        {
            speedKts = MoveSpeedKts(ctx, pose, radiusFt, turning: true);
            stepFt = StepFt(ctx, speedKts);
            travelDeg = TugKinematics.SteerTravel(pose, Move, _progress, radiusFt, stepFt);
        }

        ctx.Targets.TargetSpeed = speedKts;
        var travel = new TrueHeading(travelDeg);
        double travelTurnDeg = new TrueHeading(pose.TravelTrueDeg(Move.Kind)).SignedAngleTo(travel);
        if (Move.Kind == PushbackLegKind.Push)
        {
            aircraft.Ground.PushbackTrueHeading = travel;
            aircraft.TrueHeading = travel.ToReciprocal();
        }
        else
        {
            aircraft.Ground.PushbackTrueHeading = null;
            aircraft.TrueHeading = travel;
        }

        SteerTowbar(aircraft, travelTurnDeg, stepFt);
    }

    /// <summary>
    /// Points the towbar for the step just flown — <see cref="AircraftGroundOps.TowbarTrueHeading"/>, the direction
    /// from the nose gear out along the towbar to the tug — off the nose heading this tick was driven to.
    ///
    /// <para>The tow is a bicycle on the wheelbase <c>L</c>: the type's FAA figure, or the same fallback
    /// <see cref="TugKinematics.TurnRadiusFt"/> uses for a type that has none. The step turned the direction of travel
    /// by <c>Δψ</c> over its length, and the nose turns with it the same way on both kinds, so the path's curvature is
    /// <c>κ = Δψ / step</c> and the nose gear stands <c>δ = atan(L·κ)</c> off the fuselage axis.</para>
    ///
    /// <para>Which side of the axis the tug is on is the kinds' difference. On a pull, turning right steers the nose
    /// gear right and the tug sits ahead and to the right: <c>nose + δ</c>. On a push the nose gear is travelling
    /// backwards — its velocity in the fuselage frame is <c>(−v, ωL)</c> — so its wheel line lies
    /// <c>−atan(ωL / v)</c> off the axis: a nose yawing right means the tug has swung to the aircraft's <em>left</em>,
    /// at <c>nose − δ</c>.</para>
    ///
    /// <para>A step of no length leaves the towbar where it stood: a dwell, a controller hold and a standstill neither
    /// move the aircraft nor straighten the tug.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft under tow, with this tick's nose heading already set.</param>
    /// <param name="travelTurnDeg">How far the step turned the direction of travel, degrees, positive clockwise.</param>
    /// <param name="stepFt">The step's length, feet.</param>
    private void SteerTowbar(AircraftState aircraft, double travelTurnDeg, double stepFt)
    {
        if (stepFt <= 0.0)
        {
            return;
        }

        double? recordedWheelbaseFt = Data.Faa.FaaAircraftDatabase.Get(aircraft.AircraftType)?.WheelbaseFt;
        double wheelbaseFt =
            (recordedWheelbaseFt is { } recorded && (recorded > 0.0)) ? recorded : TugKinematics.TurnRadiusFt(aircraft.AircraftType, tight: false);
        double curvaturePerFt = (travelTurnDeg / RadToDeg) / stepFt;
        double steerDeg = Math.Atan(wheelbaseFt * curvaturePerFt) * RadToDeg;
        double towbarSide = Move.Kind == PushbackLegKind.Push ? -1.0 : 1.0;
        aircraft.Ground.TowbarTrueHeading = new TrueHeading(aircraft.TrueHeading.Degrees + (towbarSide * steerDeg));
    }

    /// <summary>The distance physics will move the aircraft this tick toward a target speed, feet.</summary>
    private static double StepFt(PhaseContext ctx, double targetKts)
    {
        AircraftState aircraft = ctx.Aircraft;
        double stepKts = Math.Min(Math.Min(aircraft.IndicatedAirspeed, targetKts), aircraft.Ground.SpeedLimit ?? double.PositiveInfinity);
        return Math.Max(0.0, stepKts) * ctx.DeltaSeconds / 3600.0 * GeoMath.FeetPerNm;
    }

    /// <summary>
    /// Whether a step turned the direction of travel by more than <see cref="TurningStepFraction"/> of the most a
    /// step of <paramref name="stepFt"/> may turn it. A step of zero length never turns.
    /// </summary>
    private bool IsTurningStep(TugPose pose, double travelDeg, double stepFt, double radiusFt)
    {
        double maxTurnDeg = (stepFt / radiusFt) * RadToDeg;
        double turnedDeg = new TrueHeading(travelDeg).AbsAngleTo(new TrueHeading(pose.TravelTrueDeg(Move.Kind)));
        return turnedDeg > (TurningStepFraction * maxTurnDeg);
    }

    /// <summary>
    /// The tug's speed this tick: the push speed, or the alignment creep once a creep move is within the spot
    /// pull-forward distance of its end; while turning, scaled by R / (R + half-span) so the outer wingtip keeps
    /// that pace (a judgement call, AC 00-65A §11.14: towing no faster than the walking team); braked down that
    /// same wingtip cap on the run in to the next turn the move makes; and, on a move that ends at a standstill,
    /// braked onto <see cref="FinalApproachKts"/> for the last <see cref="FinalApproachFt"/> before the planned
    /// end. Both approaches are <see cref="BrakeCurveKts"/>, so the tug arrives at the speed it needs instead of
    /// dropping onto it at the boundary. A move that continues into the next one takes no end-of-move curve: it is
    /// not stopping there, and the crawl would restart the whole tow from walking pace at every boundary.
    /// </summary>
    private double MoveSpeedKts(PhaseContext ctx, TugPose pose, double radiusFt, bool turning)
    {
        string type = ctx.Aircraft.AircraftType;
        double remainingFt = FeetBetween(pose.Position, PlannedEnd);
        double baseKts = BaseSpeedKts(type, ctx.Category, remainingFt);
        double halfSpanFt = TugMovePlanner.WingspanFt(type) / 2.0;
        double turningCapKts = baseKts * radiusFt / (radiusFt + halfSpanFt);
        double decelKtPerSec = CategoryPerformance.TugDecelRate(ctx.Category);
        double speedKts = turning ? turningCapKts : baseKts;

        if (NextTurnAlongFt(ctx.Aircraft, radiusFt) is { } turnAtFt)
        {
            speedKts = Math.Min(speedKts, BrakeCurveKts(turnAtFt, turningCapKts, decelKtPerSec));
        }

        return ContinuesIntoNextMove ? speedKts : Math.Min(speedKts, BrakeCurveKts(remainingFt - FinalApproachFt, FinalApproachKts, decelKtPerSec));
    }

    /// <summary>
    /// The speed this move commands from where the aircraft stands, knots: <see cref="CategoryPerformance.PushbackSpeed"/>,
    /// or <see cref="CategoryPerformance.PushbackAlignSpeed"/> once a creep move is inside the spot pull-forward distance
    /// of its end. The turn and stop curves <see cref="MoveSpeedKts"/> lays over it only ever lower it, so this is the
    /// pace the move is asking for right now.
    ///
    /// <para><see cref="GroundConflictDetector"/> reads it to tell a tow that is slowing for a neighbour from one running
    /// at its commanded pace: a limit at or above this is not slowing the move at all, and a creep move's last stretch is
    /// commanded well under the push speed.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <returns>The speed the move commands from the live pose, knots.</returns>
    public double CommandedSpeedKts(AircraftState aircraft) =>
        BaseSpeedKts(aircraft.AircraftType, AircraftCategorization.Categorize(aircraft.AircraftType), FeetBetween(aircraft.Position, PlannedEnd));

    /// <summary>The push speed, or a creep move's alignment speed once it is within the pull-forward distance of its end.</summary>
    private double BaseSpeedKts(string aircraftType, AircraftCategory category, double remainingFt) =>
        (Move.Creep && (remainingFt <= TugMovePlanner.SpotPullForwardFt(aircraftType)))
            ? CategoryPerformance.PushbackAlignSpeed(category)
            : CategoryPerformance.PushbackSpeed(category);

    /// <summary>
    /// How far along the rest of the move the next turning step lies, feet: the along-path distance of the last
    /// sample of <see cref="RemainingPath"/> still running straight, zero when the move is turning here and now.
    /// Null when nothing left of the move turns. A pair of samples counts as turning by the same test a live step
    /// does (<see cref="IsTurningStep"/>): more than <see cref="TurningStepFraction"/> of the most a step that long
    /// may turn the aircraft.
    ///
    /// <para>The look-ahead is this move's alone — it passes no continuation — so a turn that opens the next move is
    /// entered at the boundary speed and braked onto the wingtip cap from there.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <param name="radiusFt">The turn radius the move is flown on, feet.</param>
    /// <returns>The distance to the next turn, or null when the rest of the move is straight.</returns>
    private double? NextTurnAlongFt(AircraftState aircraft, double radiusFt)
    {
        IReadOnlyList<(TugPose Pose, double AlongFt)> path = RemainingPath(aircraft, []);
        for (int i = 1; i < path.Count; i++)
        {
            double stepFt = path[i].AlongFt - path[i - 1].AlongFt;
            if (stepFt <= 0.0)
            {
                continue;
            }

            double turnedDeg = new TrueHeading(path[i - 1].Pose.NoseTrueDeg).AbsAngleTo(new TrueHeading(path[i].Pose.NoseTrueDeg));
            if (turnedDeg > (TurningStepFraction * (stepFt / radiusFt) * RadToDeg))
            {
                return path[i - 1].AlongFt;
            }
        }

        return null;
    }

    /// <summary>
    /// The speed a tug braking at <paramref name="decelKtPerSec"/> may hold <paramref name="distanceFt"/> before a
    /// point it has to be down to <paramref name="endSpeedKts"/> at: v = sqrt(v_end² + 2·a·d), the distance
    /// converted from feet to knot-seconds. At or past the point it is the end speed itself.
    /// </summary>
    /// <param name="distanceFt">Distance still to run before the point, feet; at or below zero gives the end speed.</param>
    /// <param name="endSpeedKts">The speed to arrive at, knots.</param>
    /// <param name="decelKtPerSec">The braking rate, knots per second.</param>
    /// <returns>The highest speed that still arrives at <paramref name="endSpeedKts"/>, knots.</returns>
    private static double BrakeCurveKts(double distanceFt, double endSpeedKts, double decelKtPerSec) =>
        Math.Sqrt((endSpeedKts * endSpeedKts) + (2.0 * decelKtPerSec * Math.Max(0.0, distanceFt) / FtPerSecPerKt));

    /// <summary>
    /// Finishes a restore from a snapshot written before tug moves existed, now that the live pose is known:
    /// restarts the move from here, works out how much of a simple push is still owed, and replaces a provisional
    /// planned end with a simulation of the move from here.
    /// </summary>
    private void ResolvePending(AircraftState aircraft, TugPose pose)
    {
        string type = aircraft.AircraftType;
        if (_pendingPushedFrom is { } pushedFrom)
        {
            double pushedFt = IsUnset(pushedFrom) ? 0.0 : FeetBetween(pushedFrom, pose.Position);
            _move = TugMove.Straight(Move.Kind, Math.Max(0.0, TugMovePlanner.SimplePushbackFt(type) - pushedFt));
            _pendingPushedFrom = null;
        }

        _progress = TugMoveProgress.Begin(pose, Move);
        _lastPosition = pose.Position;
        _movePathCache.Invalidate();
        _runPathCache.Invalidate();
        if (Move.Shape is TugMoveShape.Straight or TugMoveShape.TurnTo)
        {
            _plannedEnd = TugKinematics.Simulate(pose, [Move], type, TugMovePlanner.StepFt).End.Position;
        }

        _progressPending = false;
        Log.LogDebug(
            "[Push] {Callsign}: resumed a pre-tug-move pushback as {Kind} {Shape} (straight {DistanceFt:F1} ft), "
                + "plannedEnd=({EndLat:F6},{EndLon:F6})",
            aircraft.Callsign,
            Move.Kind,
            Move.Shape,
            Move.StraightDistanceFt,
            PlannedEnd.Lat,
            PlannedEnd.Lon
        );
    }

    private void TraceProgress(PhaseContext ctx)
    {
        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog < LogIntervalSeconds)
        {
            return;
        }

        _timeSinceLastLog = 0;
        AircraftState aircraft = ctx.Aircraft;
        Log.LogTrace(
            "[Push] {Callsign}: {Kind} {Shape} moved={Moved:F1}ft, gs={Gs:F1}kts, pushHdg={PushHdg:F0}, noseHdg={NoseHdg:F0}, toEnd={ToEnd:F1}ft",
            aircraft.Callsign,
            Move.Kind,
            Move.Shape,
            _progress.DistanceFt,
            aircraft.GroundSpeed,
            aircraft.Ground.PushbackTrueHeading?.Degrees ?? 0,
            aircraft.TrueHeading.Degrees,
            FeetBetween(aircraft.Position, PlannedEnd)
        );
    }

    private static TugPose PoseOf(AircraftState aircraft) => new(aircraft.Position, aircraft.TrueHeading.Degrees);

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    /// <summary>A pre-tug-move snapshot recorded a start of (0, 0) for a pushback that had not started.</summary>
    private static bool IsUnset(LatLon position) => (position.Lat == 0.0) && (position.Lon == 0.0);

    public override PhaseDto ToSnapshot() =>
        new PushbackPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            Kind = Move.Kind,
            Shape = Move.Shape,
            StraightDistanceFt = Move.StraightDistanceFt,
            PointLatitude = Move.Point.Lat,
            PointLongitude = Move.Point.Lon,
            LineTravelTrueDeg = Move.LineTravelTrueDeg,
            StopAtLatitude = Move.StopAt?.Lat,
            StopAtLongitude = Move.StopAt?.Lon,
            FacingTrueDeg = Move.FacingTrueDeg,
            Tight = Move.Tight,
            Creep = Move.Creep,
            DwellBefore = Move.DwellBefore,
            StartsAtStand = StartsAtStand,
            ContinuesIntoNextMove = ContinuesIntoNextMove,
            ContinuesStandPushOff = ContinuesStandPushOff,
            PlannedEndLatitude = PlannedEnd.Lat,
            PlannedEndLongitude = PlannedEnd.Lon,
            AmendmentGoalKind = Amendment?.GoalKind,
            AmendmentNodeId = Amendment?.NodeId,
            AmendmentTaxiway = Amendment?.TaxiwayName,
            AmendmentStandLatitude = Amendment?.StandStart.Position.Lat,
            AmendmentStandLongitude = Amendment?.StandStart.Position.Lon,
            AmendmentStandNoseTrueDeg = Amendment?.StandStart.NoseTrueDeg,
            ProgressStartLatitude = _progress.Start.Lat,
            ProgressStartLongitude = _progress.Start.Lon,
            ProgressStartTravelTrueDeg = _progress.StartTravelTrueDeg,
            ProgressDistanceFt = _progress.DistanceFt,
            ProgressCaptured = _progress.Captured,
            ProgressMaxTravelDeviationDeg = _progress.MaxTravelDeviationDeg,
            LastLatitude = _lastPosition.Lat,
            LastLongitude = _lastPosition.Lon,
            DwellElapsedSeconds = _dwellElapsedSeconds,
            TimeSinceLastLog = _timeSinceLastLog,
            ProgressPending = _progressPending,
            PendingPushedFromLatitude = _pendingPushedFrom?.Lat,
            PendingPushedFromLongitude = _pendingPushedFrom?.Lon,
        };

    public static PushbackPhase FromSnapshot(PushbackPhaseDto dto)
    {
        PushbackPhase phase = dto.Shape is { } shape ? FromMoveSnapshot(dto, shape) : FromLegacySnapshot(dto);
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }

    private static PushbackPhase FromMoveSnapshot(PushbackPhaseDto dto, TugMoveShape shape)
    {
        var point = new LatLon(dto.PointLatitude, dto.PointLongitude);
        LatLon? stopAt = (dto.StopAtLatitude, dto.StopAtLongitude) is ({ } stopLat, { } stopLon) ? new LatLon(stopLat, stopLon) : null;
        TugMove move = shape switch
        {
            TugMoveShape.Straight => TugMove.Straight(dto.Kind, dto.StraightDistanceFt),
            TugMoveShape.ToPoint => TugMove.ToPoint(dto.Kind, point),
            TugMoveShape.ViaLine => TugMove.ViaLine(dto.Kind, point, dto.LineTravelTrueDeg, stopAt),
            TugMoveShape.TurnTo => TugMove.TurnTo(dto.Kind, dto.FacingTrueDeg),
            _ => throw new InvalidOperationException($"Unknown tug move shape {shape} in a pushback snapshot"),
        };

        var phase = new PushbackPhase
        {
            Move = move with { Tight = dto.Tight, Creep = dto.Creep, DwellBefore = dto.DwellBefore },
            PlannedEnd = new LatLon(dto.PlannedEndLatitude, dto.PlannedEndLongitude),
            StartsAtStand = dto.StartsAtStand,
            ContinuesIntoNextMove = dto.ContinuesIntoNextMove,
            ContinuesStandPushOff = dto.ContinuesStandPushOff,
            Amendment = AmendmentFromSnapshot(dto),
        };
        phase._progress = new TugMoveProgress(
            new LatLon(dto.ProgressStartLatitude, dto.ProgressStartLongitude),
            dto.ProgressStartTravelTrueDeg,
            dto.ProgressDistanceFt,
            dto.ProgressCaptured,
            dto.ProgressMaxTravelDeviationDeg
        );
        phase._lastPosition = new LatLon(dto.LastLatitude, dto.LastLongitude);
        phase._dwellElapsedSeconds = dto.DwellElapsedSeconds;
        phase._progressPending = dto.ProgressPending;
        phase._pendingPushedFrom = (dto.PendingPushedFromLatitude, dto.PendingPushedFromLongitude) is ({ } fromLat, { } fromLon)
            ? new LatLon(fromLat, fromLon)
            : null;
        return phase;
    }

    private static TugAmendment? AmendmentFromSnapshot(PushbackPhaseDto dto)
    {
        if (
            (dto.AmendmentGoalKind is not { } kind)
            || (dto.AmendmentStandLatitude is not { } lat)
            || (dto.AmendmentStandLongitude is not { } lon)
            || (dto.AmendmentStandNoseTrueDeg is not { } nose)
        )
        {
            return null;
        }

        return new TugAmendment(kind, dto.AmendmentNodeId, dto.AmendmentTaxiway, new TugPose(new LatLon(lat, lon), nose));
    }

    /// <summary>
    /// Restores a pushback written before tug moves existed as the equivalent move, finished on the first tick from
    /// the live pose. A push with a target is a move onto it (<see cref="LegacyTargetedMove"/>); a facing alone is a
    /// turn; neither is a straight push for the clearance still owed. The planned end of the last two is provisional
    /// until the first tick simulates the move from where the aircraft is.
    /// </summary>
    private static PushbackPhase FromLegacySnapshot(PushbackPhaseDto dto)
    {
        var start = new LatLon(dto.LegacyStartLat ?? 0.0, dto.LegacyStartLon ?? 0.0);
        LatLon? target = LegacyPoint(dto.LegacyTargetLatitude, dto.LegacyTargetLongitude);
        bool owesClearance = (target is null) && (dto.LegacyTargetHeading is null);
        (TugMove? move, LatLon plannedEnd) = target is { } point
            ? LegacyTargetedMove(dto, point)
            : (LegacyUntargetedMove(dto.LegacyTargetHeading), start);

        // A pre-tug-move snapshot carries a single pushback, never a chain, so it always ends at a standstill and
        // nothing was ever flown through from a push-off.
        var phase = new PushbackPhase
        {
            Move = move,
            PlannedEnd = plannedEnd,
            ContinuesIntoNextMove = false,
            ContinuesStandPushOff = false,
        };
        phase._progressPending = true;
        phase._pendingPushedFrom = owesClearance ? start : null;
        return phase;
    }

    /// <summary>
    /// A pre-tug-move pushback with a target: one that had reached it completes on its first tick; one pulling
    /// forward onto a spot creeps along the spot's line onto the rest point; otherwise a line capture along the
    /// facing ending on the target, or a to-point with no facing. A pull-forward still pending behind the target is
    /// dropped, so the aircraft stops at the staging point.
    /// </summary>
    private static (TugMove Move, LatLon PlannedEnd) LegacyTargetedMove(PushbackPhaseDto dto, LatLon target)
    {
        PushbackLegKind kind = dto.Kind;
        LatLon? pullForward = LegacyPoint(dto.LegacyPullForwardLatitude, dto.LegacyPullForwardLongitude);
        if (dto.LegacyReachedTarget == true)
        {
            return (TugMove.Straight(kind, 0.0), target);
        }

        if ((dto.LegacyPullingForward == true) && (pullForward is { } rest))
        {
            return (LegacyMoveOnto(PushbackLegKind.Pull, rest, dto.LegacyTargetHeading) with { Creep = true }, rest);
        }

        if (pullForward is not null)
        {
            Log.LogWarning(
                "[Push] Restoring a pre-tug-move spot pushback that had not reached its staging point: the pull forward onto the "
                    + "spot is dropped, and the aircraft stops at the staging point ({Lat:F6},{Lon:F6})",
                target.Lat,
                target.Lon
            );
        }

        return (LegacyMoveOnto(kind, target, dto.LegacyTargetHeading), target);
    }

    /// <summary>A capture of the line through the point along the nose heading, stopping on it; a to-point without one.</summary>
    private static TugMove LegacyMoveOnto(PushbackLegKind kind, LatLon point, double? noseTrueDeg) =>
        noseTrueDeg is { } nose ? TugMove.ViaLine(kind, point, TugKinematics.FlipForKind(nose, kind), point) : TugMove.ToPoint(kind, point);

    /// <summary>
    /// A pre-tug-move pushback with no target: a push turning onto its facing, or a straight push whose distance the
    /// first tick works out from how far the push had already gone.
    /// </summary>
    private static TugMove LegacyUntargetedMove(double? facingTrueDeg) =>
        facingTrueDeg is { } facing ? TugMove.TurnTo(PushbackLegKind.Push, facing) : TugMove.Straight(PushbackLegKind.Push, 0.0);

    private static LatLon? LegacyPoint(double? lat, double? lon) => (lat, lon) is ({ } a, { } b) ? new LatLon(a, b) : null;
}
