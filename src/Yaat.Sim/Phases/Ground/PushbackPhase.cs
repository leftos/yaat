using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft pushes back from parking at pushback speed.
/// Sets AircraftState.PushbackHeading so FlightPhysics moves the aircraft
/// backward while the nose heading stays forward (or rotates to target).
/// Three modes:
///   1. No target: push straight back ~80 feet.
///   2. Heading only: push back along a curved arc while rotating nose to target heading.
///   3. Target position (taxiway): arc toward target, then optionally rotate to heading.
/// </summary>
public sealed class PushbackPhase : Phase
{
    private const double DefaultPushbackDistanceNm = 0.015;
    private const double TargetReachedThresholdNm = 0.005;
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

    public int? TargetHeading { get; init; }
    public double? TargetLatitude { get; init; }
    public double? TargetLongitude { get; init; }

    public override string Name => "Pushback";

    public override void OnStart(PhaseContext ctx)
    {
        _startLat = ctx.Aircraft.Latitude;
        _startLon = ctx.Aircraft.Longitude;

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
            ctx.Aircraft.PushbackTrueHeading = ctx.Aircraft.TrueHeading.ToReciprocal();
            ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
        }
        else
        {
            double diff = alignmentHeading.Value.AbsAngleTo(ctx.Aircraft.TrueHeading);
            if (diff <= AlignmentThresholdDeg)
            {
                _isAligned = true;
                ctx.Aircraft.PushbackTrueHeading = ctx.Aircraft.TrueHeading.ToReciprocal();
                ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
            }
            else
            {
                _isAligned = false;
                ctx.Aircraft.PushbackTrueHeading = null;
                ctx.Targets.TargetSpeed = 0;
            }
        }

        ctx.Logger.LogDebug(
            "[Push] {Callsign}: started, aligned={Aligned}, pushHdg={PushHdg}, noseHdg={NoseHdg:F0}, targetHdg={TargetHdg}, pos=({Lat:F6},{Lon:F6})",
            ctx.Aircraft.Callsign,
            _isAligned,
            ctx.Aircraft.PushbackTrueHeading?.Degrees.ToString("F0") ?? "null",
            ctx.Aircraft.TrueHeading.Degrees,
            TargetHeading?.ToString() ?? "none",
            _startLat,
            _startLon
        );
        if (TargetLatitude is not null && TargetLongitude is not null)
        {
            ctx.Logger.LogDebug(
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
        if (ctx.Aircraft.IsHeld)
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
                ctx.Aircraft.PushbackTrueHeading = null;
                TurnNoseToward(ctx, alignmentHeading.Value, turnRate);
                double diff = alignmentHeading.Value.AbsAngleTo(ctx.Aircraft.TrueHeading);
                if (diff <= AlignmentThresholdDeg)
                {
                    _isAligned = true;
                    ctx.Aircraft.PushbackTrueHeading = ctx.Aircraft.TrueHeading.ToReciprocal();
                    ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
                    _startLat = ctx.Aircraft.Latitude;
                    _startLon = ctx.Aircraft.Longitude;
                    ctx.Logger.LogDebug("[Push] {Callsign}: alignment complete, starting push", ctx.Aircraft.Callsign);
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
            ctx.Aircraft.PushbackTrueHeading = null;
        }
        else
        {
            ctx.Targets.TargetSpeed = CategoryPerformance.PushbackSpeed(ctx.Category);
        }

        bool result;
        if (TargetLatitude is not null && TargetLongitude is not null)
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
            double distPushed = GeoMath.DistanceNm(_startLat, _startLon, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
            ctx.Logger.LogDebug(
                "[Push] {Callsign}: dist={Dist:F4}nm, gs={Gs:F1}kts, pushHdg={PushHdg:F0}, noseHdg={NoseHdg:F0}, pos=({Lat:F6},{Lon:F6})",
                ctx.Aircraft.Callsign,
                distPushed,
                ctx.Aircraft.GroundSpeed,
                ctx.Aircraft.PushbackTrueHeading?.Degrees ?? 0,
                ctx.Aircraft.TrueHeading.Degrees,
                ctx.Aircraft.Latitude,
                ctx.Aircraft.Longitude
            );
        }

        return result;
    }

    private bool TickTargetedPushback(PhaseContext ctx, double turnRate)
    {
        if (!_reachedTarget)
        {
            double dist = GeoMath.DistanceNm(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLatitude!.Value, TargetLongitude!.Value);

            if (dist <= TargetReachedThresholdNm)
            {
                ctx.Aircraft.Latitude = TargetLatitude!.Value;
                ctx.Aircraft.Longitude = TargetLongitude!.Value;
                ctx.Aircraft.IndicatedAirspeed = 0;
                ctx.Targets.TargetSpeed = 0;
                _reachedTarget = true;
                ctx.Logger.LogDebug("[Push] {Callsign}: reached target position, rotating to heading", ctx.Aircraft.Callsign);
            }
            else
            {
                // Gradually steer PushbackHeading toward the target in an arc
                // (tug curving the tail toward the taxiway, not a straight-line slide).
                double bearingToTarget = GeoMath.BearingTo(
                    ctx.Aircraft.Latitude,
                    ctx.Aircraft.Longitude,
                    TargetLatitude!.Value,
                    TargetLongitude!.Value
                );
                double maxArcTurn = turnRate * ctx.DeltaSeconds;
                ctx.Aircraft.PushbackTrueHeading = GeoMath.TurnHeadingToward(
                    ctx.Aircraft.PushbackTrueHeading ?? ctx.Aircraft.TrueHeading.ToReciprocal(),
                    bearingToTarget,
                    maxArcTurn
                );
            }

            // Delay nose rotation until most of the push is complete
            if (TargetHeading is { } tgt && !_reachedTarget)
            {
                double distFromStart = GeoMath.DistanceNm(_startLat, _startLon, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
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

    private bool TickSimplePushback(PhaseContext ctx, double turnRate)
    {
        if (TargetHeading is { } tgt)
        {
            bool headingReached = TurnNoseToward(ctx, new TrueHeading(tgt), turnRate);

            // Couple pushback direction to nose after rotation: as the nose rotates, the arc curves.
            ctx.Aircraft.PushbackTrueHeading = ctx.Aircraft.TrueHeading.ToReciprocal();

            double distPushed = GeoMath.DistanceNm(_startLat, _startLon, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
            return headingReached && distPushed >= DefaultPushbackDistanceNm;
        }

        double dist = GeoMath.DistanceNm(_startLat, _startLon, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
        return dist >= DefaultPushbackDistanceNm;
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
            double bearingToTarget = GeoMath.BearingTo(ctx.Aircraft.Latitude, ctx.Aircraft.Longitude, TargetLatitude.Value, TargetLongitude.Value);
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
        double distPushed = GeoMath.DistanceNm(_startLat, _startLon, ctx.Aircraft.Latitude, ctx.Aircraft.Longitude);
        ctx.Logger.LogDebug(
            "[Push] {Callsign}: OnEnd ({Status}), total dist={Dist:F4}nm, hdg={Hdg:F0}",
            ctx.Aircraft.Callsign,
            endStatus,
            distPushed,
            ctx.Aircraft.TrueHeading.Degrees
        );

        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Aircraft.PushbackTrueHeading = null;
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.AirTaxi => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Land => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected,
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
        };
        phase._startLat = dto.StartLat;
        phase._startLon = dto.StartLon;
        phase._totalDistToTarget = dto.TotalDistToTarget;
        phase._reachedTarget = dto.ReachedTarget;
        phase._isAligned = dto.IsAligned;
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
