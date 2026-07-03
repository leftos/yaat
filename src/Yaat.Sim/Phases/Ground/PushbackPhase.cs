using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft pushes back from parking at pushback speed.
/// Sets AircraftState.PushbackHeading so FlightPhysics moves the aircraft
/// backward while the nose heading stays forward (or rotates to target).
/// Three modes:
///   1. No target: push straight back by <see cref="CategoryPerformance.SimplePushbackDistanceNm"/>
///      (≈1.3× aircraft length) so the aircraft clears its gate.
///   2. Heading only: push back along a curved arc while rotating nose to target heading.
///   3. Target position (taxiway): arc toward target, then optionally rotate to heading.
/// A spot pushback (<see cref="PullForwardLatitude"/> set) adds a second leg: the reverse target is a
/// staging point behind the marking, and once reached the tug pulls the aircraft FORWARD onto the spot so
/// the nosewheel lines up on the mark, nose out.
/// </summary>
public sealed class PushbackPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("PushbackPhase");

    private const double TargetReachedThresholdNm = 0.0005;
    private const double HeadingReachedDeg = 0.5;
    private const double LogIntervalSeconds = 3.0;
    private const double NoseRotationProgressThreshold = 0.6;
    private const double AlignmentThresholdDeg = 20.0;

    private double _startLat;
    private double _startLon;
    private double _totalDistToTarget;
    private bool _reachedTarget;
    private bool _isAligned;
    private double _timeSinceLastLog;

    public int? TargetHeading { get; set; }
    public double? TargetLatitude { get; init; }
    public double? TargetLongitude { get; init; }

    /// <summary>
    /// Optional second-leg target for a spot pushback: after reversing to the staging point
    /// (<see cref="TargetLatitude"/>/<see cref="TargetLongitude"/>, set behind the marking), the tug pulls
    /// the aircraft FORWARD onto this point so the nosewheel lines up on the spot. Null for every other
    /// pushback (gate/taxiway/simple), which stop at the reverse target.
    /// </summary>
    public double? PullForwardLatitude { get; init; }
    public double? PullForwardLongitude { get; init; }

    private bool _pullingForward;

    /// <summary>
    /// Updates the target facing heading mid-pushback. Returns false if the nose
    /// has already begun rotating to the prior target (the "turn" the controller
    /// can no longer revise). Simple-mode is gated on alignment; targeted-mode
    /// is gated on the same 60% progress threshold used in TickTargetedPushback.
    /// </summary>
    public bool TryUpdateTargetHeading(int? newHeading, PhaseContext ctx)
    {
        bool isTargeted = TargetLatitude is not null && TargetLongitude is not null;

        if (isTargeted)
        {
            if (_reachedTarget || _pullingForward)
            {
                return false;
            }

            double distFromStart = GeoMath.DistanceNm(new LatLon(_startLat, _startLon), ctx.Aircraft.Position);
            double progress = _totalDistToTarget > 0.001 ? distFromStart / _totalDistToTarget : 1.0;
            if (progress >= NoseRotationProgressThreshold)
            {
                return false;
            }
        }
        else
        {
            if (_isAligned)
            {
                return false;
            }
        }

        int? prior = TargetHeading;
        TargetHeading = newHeading;
        Log.LogDebug(
            "[Push] {Callsign}: face heading amended {Prior} → {New}",
            ctx.Aircraft.Callsign,
            prior?.ToString() ?? "none",
            newHeading?.ToString() ?? "none"
        );
        return true;
    }

    public override string Name => "Pushback";

    public override void OnStart(PhaseContext ctx)
    {
        _startLat = ctx.Aircraft.Position.Lat;
        _startLon = ctx.Aircraft.Position.Lon;

        ctx.Targets.TargetTrueHeading = null;
        ctx.Aircraft.IsOnGround = true;

        if (TargetLatitude is not null && TargetLongitude is not null)
        {
            _totalDistToTarget = GeoMath.DistanceNm(_startLat, _startLon, TargetLatitude.Value, TargetLongitude.Value);
        }

        TrueHeading? alignmentHeading = ComputeAlignmentHeading(ctx);
        if (alignmentHeading is null)
        {
            // Simple pushback with no heading — no alignment needed
            _isAligned = true;
            ctx.Aircraft.Ground.PushbackTrueHeading = ctx.Aircraft.TrueHeading.ToReciprocal();
            ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
        }
        else
        {
            double diff = alignmentHeading.Value.AbsAngleTo(ctx.Aircraft.TrueHeading);
            if (diff <= AlignmentThresholdDeg)
            {
                _isAligned = true;
                ctx.Aircraft.Ground.PushbackTrueHeading = ctx.Aircraft.TrueHeading.ToReciprocal();
                ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
            }
            else
            {
                _isAligned = false;
                ctx.Aircraft.Ground.PushbackTrueHeading = null;
                ctx.Targets.TargetSpeed = 0;
            }
        }

        Log.LogDebug(
            "[Push] {Callsign}: started, aligned={Aligned}, pushHdg={PushHdg}, noseHdg={NoseHdg:F0}, targetHdg={TargetHdg}, pos=({Lat:F6},{Lon:F6})",
            ctx.Aircraft.Callsign,
            _isAligned,
            ctx.Aircraft.Ground.PushbackTrueHeading?.Degrees.ToString("F0") ?? "null",
            ctx.Aircraft.TrueHeading.Degrees,
            TargetHeading?.ToString() ?? "none",
            _startLat,
            _startLon
        );
        if (TargetLatitude is not null && TargetLongitude is not null)
        {
            Log.LogDebug(
                "[Push] {Callsign}: target position ({TLat:F6},{TLon:F6}), totalDist={Dist:F4}nm",
                ctx.Aircraft.Callsign,
                TargetLatitude.Value,
                TargetLongitude.Value,
                _totalDistToTarget
            );
        }
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.Ground.IsImmobile)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        double turnRate = CategoryPerformance.PushbackTurnRate(ctx.Category);

        // Alignment stage: rotate in place before pushing
        if (!_isAligned)
        {
            TrueHeading? alignmentHeading = ComputeAlignmentHeading(ctx);
            if (alignmentHeading is not null)
            {
                ctx.Targets.TargetSpeed = 0;
                ctx.Aircraft.Ground.PushbackTrueHeading = null;
                TurnNoseToward(ctx, alignmentHeading.Value, turnRate);
                double diff = alignmentHeading.Value.AbsAngleTo(ctx.Aircraft.TrueHeading);
                if (diff <= AlignmentThresholdDeg)
                {
                    _isAligned = true;
                    ctx.Aircraft.Ground.PushbackTrueHeading = ctx.Aircraft.TrueHeading.ToReciprocal();
                    ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
                    _startLat = ctx.Aircraft.Position.Lat;
                    _startLon = ctx.Aircraft.Position.Lon;
                    Log.LogDebug("[Push] {Callsign}: alignment complete, starting push", ctx.Aircraft.Callsign);
                }
            }
            return false;
        }

        // Once at target, stop all movement — only rotate nose in place.
        // Before reaching target, reassert speed so FlightPhysics can ramp
        // back up after a GroundConflictDetector limit clears.
        if (_reachedTarget)
        {
            ctx.Targets.TargetSpeed = 0;
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Aircraft.Ground.PushbackTrueHeading = null;
        }
        else if (_pullingForward)
        {
            ctx.Targets.TargetSpeed = CategoryPerformance.PushbackAlignSpeed(ctx.Category);
        }
        else
        {
            ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
        }

        bool result;
        if (_pullingForward)
        {
            result = TickPullForward(ctx, turnRate);
        }
        else if (TargetLatitude is not null && TargetLongitude is not null)
        {
            result = TickTargetedPushback(ctx, turnRate);
        }
        else
        {
            result = TickSimplePushback(ctx, turnRate);
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (!result && _timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            double distPushed = GeoMath.DistanceNm(new LatLon(_startLat, _startLon), ctx.Aircraft.Position);
            Log.LogTrace(
                "[Push] {Callsign}: dist={Dist:F4}nm, gs={Gs:F1}kts, pushHdg={PushHdg:F0}, noseHdg={NoseHdg:F0}, pos=({Lat:F6},{Lon:F6})",
                ctx.Aircraft.Callsign,
                distPushed,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.Ground.PushbackTrueHeading?.Degrees ?? 0,
                ctx.Aircraft.TrueHeading.Degrees,
                ctx.Aircraft.Position.Lat,
                ctx.Aircraft.Position.Lon
            );
        }

        return result;
    }

    private bool TickTargetedPushback(PhaseContext ctx, double turnRate)
    {
        if (!_reachedTarget)
        {
            double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, new LatLon(TargetLatitude!.Value, TargetLongitude!.Value));

            if (dist <= TargetReachedThresholdNm)
            {
                if (PullForwardLatitude is not null && PullForwardLongitude is not null && !_pullingForward)
                {
                    // Second leg: reached the staging point behind the spot; now pull forward onto the mark.
                    _pullingForward = true;
                    _startLat = ctx.Aircraft.Position.Lat;
                    _startLon = ctx.Aircraft.Position.Lon;
                    Log.LogDebug("[Push] {Callsign}: reached staging, pulling forward onto spot", ctx.Aircraft.Callsign);
                    return false;
                }

                ctx.Aircraft.IndicatedAirspeed = 0;
                ctx.Targets.TargetSpeed = 0;
                _reachedTarget = true;
                Log.LogDebug("[Push] {Callsign}: reached target position, rotating to heading", ctx.Aircraft.Callsign);
            }
            else
            {
                // Gradually steer PushbackHeading toward the target in an arc
                // (tug curving the tail toward the taxiway, not a straight-line slide).
                double bearingToTarget = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLatitude!.Value, TargetLongitude!.Value));
                double maxArcTurn = turnRate * ctx.DeltaSeconds;
                ctx.Aircraft.Ground.PushbackTrueHeading = GeoMath.TurnHeadingToward(
                    ctx.Aircraft.Ground.PushbackTrueHeading ?? ctx.Aircraft.TrueHeading.ToReciprocal(),
                    bearingToTarget,
                    maxArcTurn
                );
            }

            // Delay nose rotation until most of the push is complete
            if (TargetHeading is { } tgt && !_reachedTarget)
            {
                double distFromStart = GeoMath.DistanceNm(new LatLon(_startLat, _startLon), ctx.Aircraft.Position);
                double progress = _totalDistToTarget > 0.001 ? distFromStart / _totalDistToTarget : 1.0;
                if (progress >= NoseRotationProgressThreshold)
                {
                    TurnNoseToward(ctx, new TrueHeading(tgt), turnRate);
                }
            }
        }

        if (!_reachedTarget)
        {
            return false;
        }

        if (TargetHeading is not { } finalHdg)
        {
            return true;
        }

        return TurnNoseToward(ctx, new TrueHeading(finalHdg), turnRate);
    }

    /// <summary>
    /// Second leg of a spot pushback: creep FORWARD from the staging point onto the marking. Displacement
    /// points at the rest target (which sits ahead of the out-facing nose), so the aircraft moves nose-first
    /// while the nose is held on the out heading. Completes when the centroid reaches the rest point — a
    /// half-fuselage behind the spot, putting the nosewheel on the mark, lined up straight.
    /// </summary>
    private bool TickPullForward(PhaseContext ctx, double turnRate)
    {
        var rest = new LatLon(PullForwardLatitude!.Value, PullForwardLongitude!.Value);
        double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, rest);

        if (dist <= TargetReachedThresholdNm)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
            ctx.Aircraft.Ground.PushbackTrueHeading = null;
            _reachedTarget = true;
            Log.LogDebug("[Push] {Callsign}: pulled forward onto spot, lined up nose-out", ctx.Aircraft.Callsign);
            return true;
        }

        double bearingToRest = GeoMath.BearingTo(ctx.Aircraft.Position, rest);
        ctx.Aircraft.Ground.PushbackTrueHeading = new TrueHeading(bearingToRest);
        if (TargetHeading is { } outHdg)
        {
            TurnNoseToward(ctx, new TrueHeading(outHdg), turnRate);
        }

        return false;
    }

    private bool TickSimplePushback(PhaseContext ctx, double turnRate)
    {
        double clearanceNm = CategoryPerformance.SimplePushbackDistanceNm(ctx.Aircraft.AircraftType);
        if (TargetHeading is { } tgt)
        {
            bool headingReached = TurnNoseToward(ctx, new TrueHeading(tgt), turnRate);

            // Couple pushback direction to nose after rotation: as the nose rotates, the arc curves.
            ctx.Aircraft.Ground.PushbackTrueHeading = ctx.Aircraft.TrueHeading.ToReciprocal();

            double distPushed = GeoMath.DistanceNm(new LatLon(_startLat, _startLon), ctx.Aircraft.Position);
            return headingReached && distPushed >= clearanceNm;
        }

        double dist = GeoMath.DistanceNm(new LatLon(_startLat, _startLon), ctx.Aircraft.Position);
        return dist >= clearanceNm;
    }

    private static bool TurnNoseToward(PhaseContext ctx, TrueHeading target, double turnRate)
    {
        double maxTurn = turnRate * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, target.Degrees, maxTurn);
        return target.AbsAngleTo(ctx.Aircraft.TrueHeading) < HeadingReachedDeg;
    }

    /// <summary>
    /// Returns the heading the nose should face so the tail points at the target.
    /// Null means no alignment needed (simple pushback with no heading).
    /// </summary>
    private TrueHeading? ComputeAlignmentHeading(PhaseContext ctx)
    {
        if (TargetLatitude is not null && TargetLongitude is not null)
        {
            // Nose faces away from target so tail points at it
            double bearingToTarget = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLatitude.Value, TargetLongitude.Value));
            return new TrueHeading(bearingToTarget).ToReciprocal();
        }

        if (TargetHeading is { } hdg)
        {
            // Simple heading mode: nose should face TargetHeading (push = heading+180)
            return new TrueHeading(hdg);
        }

        return null;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        double distPushed = GeoMath.DistanceNm(new LatLon(_startLat, _startLon), ctx.Aircraft.Position);
        Log.LogDebug(
            "[Push] {Callsign}: OnEnd ({Status}), total dist={Dist:F4}nm, hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            endStatus,
            distPushed,
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
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is being pushed back; only HOLD/RES are accepted until pushback completes"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new PushbackPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            TargetHeading = TargetHeading,
            TargetLatitude = TargetLatitude,
            TargetLongitude = TargetLongitude,
            PullForwardLatitude = PullForwardLatitude,
            PullForwardLongitude = PullForwardLongitude,
            PullingForward = _pullingForward,
            StartLat = _startLat,
            StartLon = _startLon,
            TotalDistToTarget = _totalDistToTarget,
            ReachedTarget = _reachedTarget,
            IsAligned = _isAligned,
            TimeSinceLastLog = _timeSinceLastLog,
        };

    public static PushbackPhase FromSnapshot(PushbackPhaseDto dto)
    {
        var phase = new PushbackPhase
        {
            TargetHeading = dto.TargetHeading,
            TargetLatitude = dto.TargetLatitude,
            TargetLongitude = dto.TargetLongitude,
            PullForwardLatitude = dto.PullForwardLatitude,
            PullForwardLongitude = dto.PullForwardLongitude,
        };
        phase._startLat = dto.StartLat;
        phase._startLon = dto.StartLon;
        phase._totalDistToTarget = dto.TotalDistToTarget;
        phase._reachedTarget = dto.ReachedTarget;
        phase._isAligned = dto.IsAligned;
        phase._pullingForward = dto.PullingForward;
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
