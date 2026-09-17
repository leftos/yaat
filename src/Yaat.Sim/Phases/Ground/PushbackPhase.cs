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
/// field stays null and the nose is the direction of travel. <see cref="FlightPhysics"/> moves the aircraft.</para>
///
/// <para><b>Speed.</b> <see cref="CategoryPerformance.PushbackSpeed"/>, or
/// <see cref="CategoryPerformance.PushbackAlignSpeed"/> on the last stretch of a creep move; on a step that turns
/// by more than a quarter of the most it may, slowed so the outer wingtip keeps the same pace; 1 kt over the last
/// 10 ft so the final step lands inside the 1 ft stop tolerance. The move stops dead when it completes.</para>
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

    /// <summary>Within this distance of the planned end the tug slows to <see cref="FinalApproachKts"/>.</summary>
    private const double FinalApproachFt = 10.0;

    /// <summary>The last-stretch speed: a quarter-second step at 1 kt is 0.42 ft, inside the 1 ft stop tolerance.</summary>
    private const double FinalApproachKts = 1.0;

    /// <summary>Fuselage length assumed by <see cref="HasLeftTheStand"/> for a type the FAA database does not carry.</summary>
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

    // The rest of the move as last simulated, each sample with its distance along the path from where the simulation
    // started; and the last path handed out, with the pose it was built for. Neither is snapshotted.
    private List<(TugPose Pose, double AlongFt)>? _simulatedPath;
    private LatLon _simulatedFrom;
    private IReadOnlyList<(TugPose Pose, double AlongFt)>? _pathFromHere;
    private TugPose _pathFromHerePose;

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
    /// True once the aircraft is out in the lane rather than still on its stand. On the stand push-off
    /// (<see cref="StartsAtStand"/>) that is once it has moved more than half its fuselage length from where the
    /// move began — recomputed from the recorded start each time, so a snapshot restore reproduces it. Every other
    /// move starts off the stand, so it is true throughout.
    ///
    /// <para><see cref="GroundConflictDetector"/> uses this to decide whether a pushback outranks taxiing
    /// traffic. A push whose tail already occupies the lane is not worth holding — stopping it frees nothing
    /// and blocks the lane for longer — while one still on the stand can wait for the traffic to go by, which
    /// is what a ramp controller means by "hold your push, traffic in the alley".</para>
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <returns>True when the aircraft has left its stand.</returns>
    public bool HasLeftTheStand(AircraftState aircraft)
    {
        if (!StartsAtStand)
        {
            return true;
        }

        var start = _progress.Start;
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
    /// <see cref="HasLeftTheStand"/> guards against); it answers the live pose, so the floor is anchored to where the
    /// aircraft is rather than to the Gulf of Guinea.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <returns>The pose the move began at, or the live pose when the move recorded no start.</returns>
    public TugPose StartPose(AircraftState aircraft)
    {
        var start = _progress.Start;
        return (start.Lat == 0.0) && (start.Lon == 0.0)
            ? PoseOf(aircraft)
            : new TugPose(start, TugKinematics.FlipForKind(_progress.StartTravelTrueDeg, Move.Kind));
    }

    /// <summary>
    /// Where the rest of this move takes the aircraft, as poses with how far along the remaining path each one sits: the
    /// live pose first at zero feet, then the move simulated from it (a straight for the distance it still owes, any
    /// other shape as it is), no more than <see cref="RemainingPathSpacingFt"/> and <see cref="RemainingPathTurnDeg"/>
    /// apart. Only the live pose once the move is complete.
    ///
    /// <para><see cref="GroundConflictDetector"/> sweeps the aircraft's outline along this path to decide whether a
    /// parked or held neighbour is in the way, and the along-path distance of the first sample that fouls it is how far
    /// the mover has left before it has to be stopped. The simulation is kept and redone only once the aircraft has
    /// moved more than <see cref="RemainingPathRebuildFt"/> from where it ran; in between, the samples already passed
    /// are dropped and the rest are charged the distance already covered. None of it is in the snapshot: a restored
    /// phase simulates on first use.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <returns>The remaining path, starting at the live pose at zero feet.</returns>
    public IReadOnlyList<(TugPose Pose, double AlongFt)> RemainingPath(AircraftState aircraft)
    {
        var pose = PoseOf(aircraft);
        if ((_pathFromHere is not null) && (_pathFromHerePose == pose))
        {
            return _pathFromHere;
        }

        _pathFromHerePose = pose;
        if ((Status == PhaseStatus.Completed) || TugKinematics.IsComplete(pose, Move, _progress))
        {
            _pathFromHere = [(pose, 0.0)];
            return _pathFromHere;
        }

        double movedFt = _simulatedPath is null ? 0.0 : FeetBetween(_simulatedFrom, pose.Position);
        if ((_simulatedPath is null) || (movedFt > RemainingPathRebuildFt))
        {
            _simulatedPath = SimulateRemaining(aircraft.AircraftType, pose);
            _simulatedFrom = pose.Position;
            movedFt = 0.0;
        }

        var path = new List<(TugPose Pose, double AlongFt)>(_simulatedPath.Count) { (pose, 0.0) };
        foreach (var (sample, alongFt) in _simulatedPath)
        {
            if (alongFt > movedFt)
            {
                path.Add((sample, alongFt - movedFt));
            }
        }

        _pathFromHere = path;
        return path;
    }

    /// <summary>
    /// The rest of the move flown from <paramref name="pose"/>, with each sample's distance along the path; the
    /// simulation's 5 ft samples are subdivided wherever the nose turns more than <see cref="RemainingPathTurnDeg"/>
    /// between two of them (over 5 ft the chord and the arc differ by well under a tenth of a foot).
    /// </summary>
    private List<(TugPose Pose, double AlongFt)> SimulateRemaining(string aircraftType, TugPose pose)
    {
        var move =
            Move.Shape == TugMoveShape.Straight
                ? Move with
                {
                    StraightDistanceFt = Math.Max(0.0, Move.StraightDistanceFt - _progress.DistanceFt),
                }
                : Move;
        var samples = TugKinematics.Simulate(pose, [move], aircraftType, TugMovePlanner.StepFt).Moves[0].Samples;
        var path = new List<(TugPose Pose, double AlongFt)>(samples.Count * 2) { (samples[0], 0.0) };
        double alongFt = 0.0;
        for (int i = 1; i < samples.Count; i++)
        {
            var from = samples[i - 1];
            var to = samples[i];
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

    public override void OnStart(PhaseContext ctx)
    {
        var aircraft = ctx.Aircraft;
        var pose = PoseOf(aircraft);
        _progress = TugMoveProgress.Begin(pose, Move);
        _lastPosition = pose.Position;
        ctx.Targets.TargetTrueHeading = null;
        aircraft.IsOnGround = true;
        aircraft.Ground.PushbackTrueHeading = Move.Kind == PushbackLegKind.Push ? new TrueHeading(pose.TravelTrueDeg(Move.Kind)) : null;
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
        var aircraft = ctx.Aircraft;
        if (aircraft.Ground.IsImmobile)
        {
            aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        var pose = PoseOf(aircraft);
        if (_progressPending)
        {
            ResolvePending(aircraft, pose);
        }

        _progress = TugKinematics.Record(_progress, pose, Move, FeetBetween(_lastPosition, pose.Position));
        _lastPosition = pose.Position;

        if (TugKinematics.IsComplete(pose, Move, _progress))
        {
            aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
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

        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Aircraft.Ground.PushbackTrueHeading = null;
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
        var aircraft = ctx.Aircraft;
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
    }

    /// <summary>The distance physics will move the aircraft this tick toward a target speed, feet.</summary>
    private static double StepFt(PhaseContext ctx, double targetKts)
    {
        var aircraft = ctx.Aircraft;
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
    /// that pace (a judgement call, AC 00-65A §11.14: towing no faster than the walking team); and at most
    /// <see cref="FinalApproachKts"/> within <see cref="FinalApproachFt"/> of the planned end.
    /// </summary>
    private double MoveSpeedKts(PhaseContext ctx, TugPose pose, double radiusFt, bool turning)
    {
        string type = ctx.Aircraft.AircraftType;
        double remainingFt = FeetBetween(pose.Position, PlannedEnd);
        bool creeping = Move.Creep && (remainingFt <= TugMovePlanner.SpotPullForwardFt(type));
        double speedKts = creeping ? CategoryPerformance.PushbackAlignSpeed(ctx.Category) : CategoryPerformance.PushbackSpeed(ctx.Category);
        if (turning)
        {
            double halfSpanFt = TugMovePlanner.WingspanFt(type) / 2.0;
            speedKts *= radiusFt / (radiusFt + halfSpanFt);
        }

        return remainingFt <= FinalApproachFt ? Math.Min(speedKts, FinalApproachKts) : speedKts;
    }

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
        _simulatedPath = null;
        _pathFromHere = null;
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
        var aircraft = ctx.Aircraft;
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
        var phase = dto.Shape is { } shape ? FromMoveSnapshot(dto, shape) : FromLegacySnapshot(dto);
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
        var move = shape switch
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
        var target = LegacyPoint(dto.LegacyTargetLatitude, dto.LegacyTargetLongitude);
        bool owesClearance = (target is null) && (dto.LegacyTargetHeading is null);
        var (move, plannedEnd) = target is { } point ? LegacyTargetedMove(dto, point) : (LegacyUntargetedMove(dto.LegacyTargetHeading), start);

        var phase = new PushbackPhase { Move = move, PlannedEnd = plannedEnd };
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
        var kind = dto.Kind;
        var pullForward = LegacyPoint(dto.LegacyPullForwardLatitude, dto.LegacyPullForwardLongitude);
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
