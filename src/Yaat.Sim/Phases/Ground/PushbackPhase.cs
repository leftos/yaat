using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
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
///
/// <para><see cref="Kind"/> picks which end of the aircraft the tug leads with over the leg: a
/// <see cref="PushbackLegKind.Push"/> reverses it tail-first, a <see cref="PushbackLegKind.Pull"/> tows it
/// nose-first. Every single-target PUSH is a push, which is the default.</para>
/// </summary>
public sealed class PushbackPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("PushbackPhase");

    private const double TargetReachedThresholdNm = 0.0005;
    private const double HeadingReachedDeg = 0.5;
    private const double LogIntervalSeconds = 3.0;
    private const double NoseRotationProgressThreshold = 0.6;
    private const double AlignmentThresholdDeg = 20.0;

    /// <summary>Fuselage length assumed by <see cref="HasLeftTheStand"/> for a type the FAA database does not carry.</summary>
    private const double DefaultFuselageLengthFt = 110.0;

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
    /// Which end the tug leads with over this leg. A <see cref="PushbackLegKind.Push"/> reverses the aircraft
    /// tail-first along <see cref="AircraftGroundOps.PushbackTrueHeading"/>; a <see cref="PushbackLegKind.Pull"/>
    /// tows it nose-first, leaving that field null so <see cref="FlightPhysics"/> displaces it the ordinary way.
    /// Defaults to a push, which is what every PUSH command builds.
    /// </summary>
    public PushbackLegKind Kind { get; init; }

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

    /// <summary>
    /// True once the aircraft has moved more than half its fuselage length from where the push began — the
    /// point at which its tail is out in the lane rather than still on the stand. Recomputed from the recorded
    /// start each time rather than latched, so a snapshot restore reproduces it exactly; it is monotone in
    /// practice because every mode only moves away from the stand (the spot mode's pull-forward leg stops at
    /// the marking, still well clear of it).
    ///
    /// <para><see cref="GroundConflictDetector"/> uses this to decide whether a pushback outranks taxiing
    /// traffic. A push whose tail already occupies the lane is not worth holding — stopping it frees nothing
    /// and blocks the lane for longer — while one still on the stand can wait for the traffic to go by, which
    /// is what a ramp controller means by "hold your push, traffic in the alley".</para>
    /// </summary>
    public bool HasLeftTheStand(AircraftState aircraft)
    {
        // A push that has reached its target, or is pulling forward onto a spot, is out in the lane whatever
        // the distance reads: both legs re-base the start point, so measuring from it would say "still on the
        // stand" for an aircraft most of the way across the alley. Both flags ride the snapshot.
        if (_reachedTarget || _pullingForward)
        {
            return true;
        }

        if ((_startLat == 0.0) && (_startLon == 0.0))
        {
            return false;
        }

        double halfFuselageFt = (Data.Faa.FaaAircraftDatabase.Get(aircraft.AircraftType)?.LengthFt ?? DefaultFuselageLengthFt) / 2.0;
        return GeoMath.DistanceNm(new LatLon(_startLat, _startLon), aircraft.Position) * GeoMath.FeetPerNm > halfFuselageFt;
    }

    /// <summary>
    /// Where the leg the tug is currently running ends — the point the tail is being taken to. Returns false
    /// when there is no leg to report: the push has not started reversing yet (still rotating on the stand),
    /// or it has reached its target and only the nose is still turning. Recomputed from the persisted state
    /// each call, like <see cref="HasLeftTheStand"/>, so a snapshot restore reproduces it exactly.
    ///
    /// <para>A targeted push ends on its target; a spot push ends on the staging point while reversing and on
    /// the marking once it is pulling forward. A simple or heading-only push has no target, so its leg ends
    /// <see cref="CategoryPerformance.SimplePushbackDistanceNm"/> along the current push direction from where
    /// the push began — the arc curves as the nose rotates, so the end is where the push is committed to, not
    /// a promise about the intervening track.</para>
    ///
    /// <para><see cref="GroundConflictDetector"/> measures traffic against the segment from the aircraft to
    /// this point, which is how a push whose remaining track stays clear of an aircraft holding for it is
    /// allowed to finish instead of stopping nose-to-nose with it.</para>
    /// </summary>
    /// <param name="aircraft">The aircraft this phase is driving.</param>
    /// <param name="end">The leg's end point when one exists.</param>
    /// <returns>True when <paramref name="end"/> was set.</returns>
    public bool TryGetPushLegEnd(AircraftState aircraft, out LatLon end)
    {
        end = default;
        if (!_isAligned || _reachedTarget)
        {
            return false;
        }

        if (_pullingForward)
        {
            if ((PullForwardLatitude is not { } restLat) || (PullForwardLongitude is not { } restLon))
            {
                return false;
            }

            end = new LatLon(restLat, restLon);
            return true;
        }

        if ((TargetLatitude is { } targetLat) && (TargetLongitude is { } targetLon))
        {
            end = new LatLon(targetLat, targetLon);
            return true;
        }

        if (aircraft.Ground.PushbackTrueHeading is not { } pushHeading)
        {
            return false;
        }

        double clearanceNm = CategoryPerformance.SimplePushbackDistanceNm(aircraft.AircraftType);
        end = GeoMath.ProjectPoint(new LatLon(_startLat, _startLon), pushHeading, clearanceNm);
        return true;
    }

    public override void OnStart(PhaseContext ctx)
    {
        // A pull has to know where it is being towed to: the targetless modes (TickSimplePushback and the
        // no-target fallback) both reverse the aircraft, so a pull with no target would silently run as a push.
        if ((Kind == PushbackLegKind.Pull) && ((TargetLatitude is null) || (TargetLongitude is null)))
        {
            throw new InvalidOperationException(
                $"{ctx.Aircraft.Callsign}: a pull leg needs a target position — a targetless pushback reverses the aircraft"
            );
        }

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
            BeginLeg(ctx);
        }
        else
        {
            double diff = alignmentHeading.Value.AbsAngleTo(ctx.Aircraft.TrueHeading);
            if (diff <= AlignmentThresholdDeg)
            {
                _isAligned = true;
                BeginLeg(ctx);
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
                    BeginLeg(ctx);
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
            ctx.Targets.TargetSpeed = LegSpeed(ctx.Category);
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
                double bearingToTarget = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLatitude!.Value, TargetLongitude!.Value));
                SteerLeadingEndToward(ctx, bearingToTarget, turnRate);
            }

            // Delay nose rotation until most of the push is complete — a push only, because it steers the tail
            // through a separate field and the nose is free to rotate under way. A pull leads with the nose, so
            // the pursuit above owns it right up to the capture and the final facing takes over at arrival.
            if ((Kind != PushbackLegKind.Pull) && TargetHeading is { } tgt && !_reachedTarget && PastNoseRotationThreshold(ctx))
            {
                TurnNoseToward(ctx, new TrueHeading(tgt), turnRate);
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
    /// True once <see cref="NoseRotationProgressThreshold"/> of a push leg is covered — the point the nose
    /// starts rotating onto the final facing, so the turn finishes while the aircraft is still moving. A pull
    /// leg does not use it: it leads with the nose, so the pursuit arc and the final-facing rotation would both
    /// write <see cref="AircraftState.TrueHeading"/> on the same tick, turning the nose at up to twice
    /// <see cref="CategoryPerformance.PushbackTurnRate"/> and pulling against each other for the rest of the
    /// leg — the pull hands the nose over at arrival instead, and rotates onto the facing stopped on the target.
    /// </summary>
    private bool PastNoseRotationThreshold(PhaseContext ctx)
    {
        double distFromStart = GeoMath.DistanceNm(new LatLon(_startLat, _startLon), ctx.Aircraft.Position);
        double progress = _totalDistToTarget > 0.001 ? distFromStart / _totalDistToTarget : 1.0;
        return progress >= NoseRotationProgressThreshold;
    }

    /// <summary>
    /// Gradually curves the end of the aircraft the tug leads with onto the bearing to the leg's target — the
    /// pursuit arc (the tug swinging that end around), not a straight-line slide. A push leads with the tail,
    /// so it steers <see cref="AircraftGroundOps.PushbackTrueHeading"/>, the heading
    /// <see cref="FlightPhysics"/> displaces along, and leaves the nose where it is; a pull leads with the
    /// nose, so it steers the nose and leaves the pushback heading null, which is what makes the aircraft
    /// travel forward.
    /// </summary>
    private void SteerLeadingEndToward(PhaseContext ctx, double bearingToTarget, double turnRate)
    {
        if (Kind == PushbackLegKind.Pull)
        {
            TurnNoseToward(ctx, new TrueHeading(bearingToTarget), turnRate);
            return;
        }

        double maxArcTurn = turnRate * ctx.DeltaSeconds;
        ctx.Aircraft.Ground.PushbackTrueHeading = GeoMath.TurnHeadingToward(
            ctx.Aircraft.Ground.PushbackTrueHeading ?? ctx.Aircraft.TrueHeading.ToReciprocal(),
            bearingToTarget,
            maxArcTurn
        );
    }

    /// <summary>
    /// Puts the aircraft under way on its leg: a push is displaced tail-first along
    /// <see cref="AircraftGroundOps.PushbackTrueHeading"/>, so that is set to the reciprocal of the nose; a
    /// pull leaves it null and is displaced nose-first.
    /// </summary>
    private void BeginLeg(PhaseContext ctx)
    {
        ctx.Aircraft.Ground.PushbackTrueHeading = Kind == PushbackLegKind.Pull ? null : ctx.Aircraft.TrueHeading.ToReciprocal();
        ctx.Targets.TargetSpeed = LegSpeed(ctx.Category);
    }

    /// <summary>
    /// The tug's speed over this leg: the reverse speed for a push, and for a pull the slower speed the final
    /// forward alignment creep already runs at.
    /// </summary>
    private double LegSpeed(AircraftCategory category) =>
        Kind == PushbackLegKind.Pull ? CategoryPerformance.PushbackAlignSpeed(category) : CategoryPerformance.PushbackSpeed(category);

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
    /// Returns the heading the nose should face for the end the tug leads with to point at the target: away
    /// from it on a push (tail pointing at it), at it on a pull.
    /// Null means no alignment needed (simple pushback with no heading).
    /// </summary>
    private TrueHeading? ComputeAlignmentHeading(PhaseContext ctx)
    {
        if (TargetLatitude is not null && TargetLongitude is not null)
        {
            double bearingToTarget = GeoMath.BearingTo(ctx.Aircraft.Position, new LatLon(TargetLatitude.Value, TargetLongitude.Value));
            var towardTarget = new TrueHeading(bearingToTarget);
            return Kind == PushbackLegKind.Pull ? towardTarget : towardTarget.ToReciprocal();
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
            // Redirecting an attached tug is ordinary: the new move replaces this leg and every leg still queued
            // behind it, the same way a taxi clearance mid-move takes the whole move with it.
            CanonicalCommandType.PushbackMulti => CommandAcceptance.ClearsPhase,
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
            Kind = Kind,
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
            Kind = dto.Kind,
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
