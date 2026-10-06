using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Continuously follows a target aircraft on the ground.
/// Matches the target's speed with a safe following distance.
/// Completes when the target is deleted or no longer on the ground.
/// Requires PhaseContext.AircraftLookup to resolve the target.
/// </summary>
public sealed class FollowingPhase(string targetCallsign) : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("FollowingPhase");

    /// <summary>Gap at which the follower stops matching taxi speed and starts closing up on the lead (~180 ft).</summary>
    public const double FollowDistanceNm = 0.03;

    /// <summary>Gap the follower stops at behind the lead (~90 ft).</summary>
    public const double StopDistanceNm = 0.015;

    /// <summary>
    /// Walking-pace speed the follower closes the last stretch at when the lead is stopped or crawling — matching
    /// the lead's speed verbatim would freeze the follower at the follow distance behind a stationary lead.
    /// </summary>
    private const double FollowCloseUpSpeedKts = 5.0;

    private const double HoldShortDetectionNm = 0.02; // ~120 ft
    private const double HoldShortAngleThreshold = 90.0;

    /// <summary>
    /// How far (ft) beyond <see cref="HoldShortDetectionNm"/> a bar may sit off the follower's line of travel and still be
    /// one it is rolling toward: a taxiway's half-width plus the slack of steering on the lead rather than a centerline.
    /// </summary>
    private const double BarCorridorHalfWidthFt = 50.0;

    /// <summary>How close (ft) to a bar's stop the hold is taken — the same window a taxiing aircraft takes a set-back stop in.</summary>
    private const double BarStopTakeFt = GroundNavigator.SetBackStopMarginFt + 1.0;

    private const double LogIntervalSeconds = 3.0;

    private readonly string _targetCallsign = targetCallsign;
    private double _timeSinceLastLog;

    /// <summary>
    /// The runway bar the follow is stopping at, held from the tick it is first seen until the hold is taken there, so it
    /// stays the stop once the bar is off the nose — an aircraft cutting the corner onto the taxiway passes abeam the bar
    /// node with its nose still short of the hold line. Null with no bar ahead.
    /// </summary>
    private int? _latchedBarNodeId;

    /// <summary>The bearing (deg true) from the latched bar back up the taxiway edge leading into it, read when it was latched.</summary>
    private double? _latchedBarApproachDeg;

    /// <summary>
    /// The straight taxi edge the moving follower was last found on, by its end nodes (<c>Nodes[0]</c>, <c>Nodes[1]</c>):
    /// where <see cref="RefreshCurrentTaxiway"/> looks first. Null before the first find and while off every taxiway.
    /// </summary>
    private (int NodeA, int NodeB)? _taxiEdgeNodeIds;

    public string TargetCallsign => _targetCallsign;

    private IReadOnlyList<RunwayIdentifier> _crossingClearedRunways = [];
    private bool _hasBeenOnClearedRunway;

    /// <summary>
    /// Runways the crossing clearance that started this follow cleared — <c>FOLLOWG X; CROSS 1L 1R</c> releasing a
    /// follow armed at the 1L bar clears both, one clearance for the pair (7110.65 §3-7-2c). The follow does not stop
    /// at a bar protecting any of them (<see cref="CheckRunwayHoldShort"/>), except the follower's own departure bar.
    /// The clearance is used once: the first tick the aircraft is clear of every cleared runway's pavement after
    /// having been on one, it expires (<see cref="ExpireUsedCrossingClearance"/>). Empty for a follow no crossing
    /// started.
    /// </summary>
    public IReadOnlyList<RunwayIdentifier> CrossingClearedRunways
    {
        get => _crossingClearedRunways;
        init => _crossingClearedRunways = value;
    }

    public override string Name => $"Following {_targetCallsign}";

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        Log.LogDebug("[Follow] {Callsign}: following {Target}", ctx.Aircraft.Callsign, _targetCallsign);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (ctx.Aircraft.Ground.IsImmobile)
        {
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        ExpireUsedCrossingClearance(ctx);
        RefreshCurrentTaxiway(ctx);
        if (CheckRunwayHoldShort(ctx, out double? barStopCurveKts))
        {
            return true;
        }

        AircraftState? target = ctx.AircraftLookup?.Invoke(_targetCallsign);
        if (target is null || !target.IsOnGround)
        {
            Log.LogDebug(
                "[Follow] {Callsign}: target {Target} {Reason}, stopping",
                ctx.Aircraft.Callsign,
                _targetCallsign,
                target is null ? "not found" : "no longer on ground"
            );
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;

            // Insert idle ground phase so the aircraft remains in a state that accepts commands
            ctx.Aircraft.Phases?.InsertAfterCurrent(new HoldingInPositionPhase());
            return true;
        }

        double dist = GeoMath.DistanceNm(ctx.Aircraft.Position, target.Position);

        // Turn toward the target
        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, target.Position);
        double maxTurn = CategoryPerformance.GroundYawRateAtSpeed(ctx.Category, ctx.Aircraft.GroundSpeed) * ctx.DeltaSeconds;
        ctx.Aircraft.TrueHeading = GeoMath.TurnHeadingToward(ctx.Aircraft.TrueHeading, bearing, maxTurn);

        // Speed: match target with distance-based adjustment. Publish the target and let physics close
        // the gap at the ground accel/brake rates — the phase never integrates speed itself.
        if (dist <= StopDistanceNm)
        {
            ctx.Targets.TargetSpeed = 0;
        }
        else if (dist <= FollowDistanceNm)
        {
            // Brake curve to the stop gap rather than a flat close-up speed: v = sqrt(2·a·d) decays to 0
            // exactly at StopDistanceNm, so the follower rolls to a stop instead of holding 5 kt until the
            // gap trips the branch above and slamming the target to 0 (bang-bang at the boundary). The
            // lead's own speed still wins when it is moving — a follower never falls behind a rolling lead.
            double remainingToStopNm = Math.Max(0.0, dist - StopDistanceNm);
            double brakeCurveKts = Math.Sqrt(2.0 * CategoryPerformance.TaxiDecelRate(ctx.Category) * remainingToStopNm * 3600.0);
            ctx.Targets.TargetSpeed = Math.Max(target.GroundSpeed, Math.Min(FollowCloseUpSpeedKts, brakeCurveKts));
        }
        else
        {
            ctx.Targets.TargetSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        }

        // A runway bar ahead caps the follow at the braking curve onto its stop, whatever the lead is doing.
        if ((barStopCurveKts is { } curveKts) && ((ctx.Targets.TargetSpeed ?? 0.0) > curveKts))
        {
            ctx.Targets.TargetSpeed = curveKts;
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            Log.LogTrace(
                "[Follow] {Callsign}: dist={Dist:F4}nm to {Target}, gs={Gs:F1}kts, targetGs={TGs:F1}kts",
                ctx.Aircraft.Callsign,
                dist,
                _targetCallsign,
                ctx.Aircraft.GroundSpeed,
                target.GroundSpeed
            );
        }

        return false;
    }

    /// <summary>
    /// A firm stop at a runway bar publishes its own brake rate; clearing it on exit keeps that rate from carrying into the
    /// next phase, since <see cref="ControlTargets"/> persist across phases.
    /// </summary>
    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus) => ctx.Targets.DesiredDecelRate = null;

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Resume => CommandAcceptance.Allowed,
            CanonicalCommandType.CrossRunway => CommandAcceptance.Allowed,
            CanonicalCommandType.FollowGround => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is following another; only HOLD/RES, CROSS, a new FOLLOWG, or a new TAXI apply"),
        };
    }

    public override PhaseDto ToSnapshot() =>
        new FollowingPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            TargetCallsign = _targetCallsign,
            TimeSinceLastLog = _timeSinceLastLog,
            CrossingClearedRunways = [.. _crossingClearedRunways.Select(static r => r.ToString())],
            HasBeenOnClearedRunway = _hasBeenOnClearedRunway,
            LatchedBarNodeId = _latchedBarNodeId,
            LatchedBarApproachDeg = _latchedBarApproachDeg,
            TaxiEdgeNodeA = _taxiEdgeNodeIds?.NodeA,
            TaxiEdgeNodeB = _taxiEdgeNodeIds?.NodeB,
        };

    public static FollowingPhase FromSnapshot(FollowingPhaseDto dto)
    {
        var phase = new FollowingPhase(dto.TargetCallsign)
        {
            _timeSinceLastLog = dto.TimeSinceLastLog,
            CrossingClearedRunways = [.. dto.CrossingClearedRunways.Select(RunwayIdentifier.Parse)],
            _hasBeenOnClearedRunway = dto.HasBeenOnClearedRunway,
            _latchedBarNodeId = dto.LatchedBarNodeId,
            _latchedBarApproachDeg = dto.LatchedBarApproachDeg,
            _taxiEdgeNodeIds = (dto.TaxiEdgeNodeA is { } nodeA) && (dto.TaxiEdgeNodeB is { } nodeB) ? (nodeA, nodeB) : null,
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
        };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }

    /// <summary>
    /// A runway bar the follower must stop at, with the reason it holds there, the bearing (deg true) from the bar back up
    /// the taxiway edge leading into it, and how far (ft) the nose still has to roll to the hold line — the line through the
    /// bar node square to that edge — measured along the edge; negative once the nose is past the line.
    /// </summary>
    private readonly record struct BarAhead(GroundNode Node, HoldShortReason Reason, double ApproachDeg, double ToStopFt);

    /// <summary>
    /// Brake for a runway bar ahead the follower must stop at, and hold there once stopped. Returns true when the hold
    /// was taken (<see cref="HoldingShortPhase"/> + a new <see cref="FollowingPhase"/> inserted, this phase completes);
    /// otherwise <paramref name="barStopCurveKts"/> is the braking-curve cap onto the bar's stop, or null with no bar ahead.
    /// Publishes the brake rate the stop needs: the taxi rate (no override) when that makes the hold line, the max-effort
    /// rate when only that does.
    /// </summary>
    private bool CheckRunwayHoldShort(PhaseContext ctx, out double? barStopCurveKts)
    {
        barStopCurveKts = null;
        ctx.Targets.DesiredDecelRate = null;
        if (FindBarAhead(ctx) is not { } bar)
        {
            return false;
        }

        GroundStopBraking.StopBraking braking = GroundStopBraking.ChooseStopBraking(ctx, bar.ToStopFt);
        if (TryTakeHoldAtBar(ctx, bar, braking))
        {
            return true;
        }

        double maxEffortRate = CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        barStopCurveKts = braking switch
        {
            GroundStopBraking.StopBraking.Routine => GroundStopBraking.StopCurveKts(
                ctx,
                bar.ToStopFt,
                CategoryPerformance.TaxiDecelRate(ctx.Category)
            ),
            GroundStopBraking.StopBraking.MaxEffort => GroundStopBraking.StopCurveKts(ctx, bar.ToStopFt, maxEffortRate),
            _ => 0.0,
        };
        ctx.Targets.DesiredDecelRate = braking == GroundStopBraking.StopBraking.Routine ? null : maxEffortRate;
        return false;
    }

    /// <summary>
    /// The runway bar the follower must stop at, or null: the latched bar while the follower is still closing on its hold
    /// line, otherwise the nearest bar ahead it is closing on, which is then latched. Looked for out to the follower's
    /// braking distance to the bar's stop (<see cref="BarLookAheadFt"/>), so the stop is braked for at the taxi brake rate
    /// rather than met inside it. Bars the follower is already on the runway of, and bars of a runway its crossing clearance
    /// covers, are passed — except its own departure bar, which no crossing clearance covers: that runway is left by LUAW/CTO.
    /// </summary>
    private BarAhead? FindBarAhead(PhaseContext ctx)
    {
        if (ctx.GroundLayout is null)
        {
            return null;
        }

        double halfLengthFt = AircraftLength.ResolveFt(ctx.Aircraft.AircraftType) / 2.0;
        double lookAheadFt = BarLookAheadFt(ctx, halfLengthFt);
        if (LatchedBar(ctx, ctx.GroundLayout, halfLengthFt, lookAheadFt) is { } latched)
        {
            return latched;
        }

        BarAhead? nearest = null;
        double nearestDistanceFt = double.MaxValue;
        foreach (GroundNode node in ctx.GroundLayout.Nodes.Values)
        {
            if (!IsBarAhead(ctx, node, lookAheadFt) || !MustStopAt(ctx, node))
            {
                continue;
            }

            double distanceFt = GeoMath.DistanceNm(ctx.Aircraft.Position, node.Position) * GeoMath.FeetPerNm;
            if (distanceFt >= nearestDistanceFt)
            {
                continue;
            }

            BarAhead candidate = ToBarAhead(ctx, node, ApproachBearingDeg(node, ctx.Aircraft.Position), halfLengthFt);
            if (IsClosingOnHoldLine(ctx, candidate, lookAheadFt))
            {
                nearest = candidate;
                nearestDistanceFt = distanceFt;
            }
        }

        if (nearest is { } bar)
        {
            _latchedBarNodeId = bar.Node.Id;
            _latchedBarApproachDeg = bar.ApproachDeg;
            Log.LogDebug(
                "[Follow] {Callsign}: runway bar node {NodeId} ({Runway}) ahead, nose {ToStop:F1} ft from its hold line at {Speed:F1} kt",
                ctx.Aircraft.Callsign,
                bar.Node.Id,
                bar.Node.RunwayId?.ToString() ?? "unknown",
                bar.ToStopFt,
                ctx.Aircraft.GroundSpeed
            );
        }

        return nearest;
    }

    /// <summary>
    /// The latched bar, while the follower must still stop there and is still closing on its hold line; otherwise the
    /// latch is released and null returned.
    /// </summary>
    private BarAhead? LatchedBar(PhaseContext ctx, AirportGroundLayout layout, double halfLengthFt, double lookAheadFt)
    {
        if ((_latchedBarNodeId is not { } nodeId) || (_latchedBarApproachDeg is not { } approachDeg))
        {
            return null;
        }

        if (layout.Nodes.TryGetValue(nodeId, out GroundNode? node) && MustStopAt(ctx, node))
        {
            BarAhead bar = ToBarAhead(ctx, node, approachDeg, halfLengthFt);
            if (IsClosingOnHoldLine(ctx, bar, lookAheadFt))
            {
                return bar;
            }
        }

        Log.LogDebug("[Follow] {Callsign}: runway bar node {NodeId} no longer ahead; released", ctx.Aircraft.Callsign, nodeId);
        ReleaseLatchedBar();
        return null;
    }

    private void ReleaseLatchedBar()
    {
        _latchedBarNodeId = null;
        _latchedBarApproachDeg = null;
    }

    /// <summary>
    /// Whether the follower must stop at this bar: not when it is already on that runway, nor when its crossing clearance
    /// covers the runway — unless the bar is its own departure bar.
    /// </summary>
    private bool MustStopAt(PhaseContext ctx, GroundNode node) =>
        !IsAlreadyOnThatRunway(ctx, node) && (BarIsOwnDestination(ctx, node) || !IsClearedToCross(node));

    /// <summary><paramref name="node"/> as a bar ahead of the follower, its hold line square to <paramref name="approachDeg"/>.</summary>
    private static BarAhead ToBarAhead(PhaseContext ctx, GroundNode node, double approachDeg, double halfLengthFt)
    {
        HoldShortReason reason = BarIsOwnDestination(ctx, node) ? HoldShortReason.DestinationRunway : HoldShortReason.RunwayCrossing;
        LatLon nose = GeoMath.ProjectPoint(ctx.Aircraft.Position, ctx.Aircraft.TrueHeading, halfLengthFt / GeoMath.FeetPerNm);
        double noseFromBarFt = GeoMath.DistanceNm(node.Position, nose) * GeoMath.FeetPerNm;
        double offAxisRad = GeoMath.SignedBearingDifference(approachDeg, GeoMath.BearingTo(node.Position, nose)) * Math.PI / 180.0;
        return new BarAhead(node, reason, approachDeg, noseFromBarFt * Math.Cos(offAxisRad));
    }

    /// <summary>
    /// The bearing (deg true) from <paramref name="bar"/> back up the edge leading into it: of the bar node's edges, the
    /// one whose far end lies nearest in bearing to <paramref name="aircraftPosition"/>.
    /// </summary>
    private static double ApproachBearingDeg(GroundNode bar, LatLon aircraftPosition)
    {
        double towardAircraft = GeoMath.BearingTo(bar.Position, aircraftPosition);
        double approachDeg = towardAircraft;
        double nearestOffDeg = double.MaxValue;
        foreach (IGroundEdge edge in bar.Edges)
        {
            double edgeDeg = GeoMath.BearingTo(bar.Position, FarNode(edge, bar).Position);
            double offDeg = Math.Abs(GeoMath.SignedBearingDifference(towardAircraft, edgeDeg));
            if (offDeg < nearestOffDeg)
            {
                nearestOffDeg = offDeg;
                approachDeg = edgeDeg;
            }
        }

        return approachDeg;
    }

    private static GroundNode FarNode(IGroundEdge edge, GroundNode from) => edge.Nodes[0].Id == from.Id ? edge.Nodes[1] : edge.Nodes[0];

    /// <summary>
    /// Whether the follower is rolling into the bar's hold line: its heading carries it toward the line, and at that angle
    /// the nose meets the line within <paramref name="lookAheadFt"/> (or is already at or past it). A bar beside the
    /// follower's path, whose line it runs along rather than into, is not one it stops at.
    /// </summary>
    private static bool IsClosingOnHoldLine(PhaseContext ctx, BarAhead bar, double lookAheadFt)
    {
        double towardLineDeg = (bar.ApproachDeg + 180.0) % 360.0;
        double offLineRad = GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, towardLineDeg) * Math.PI / 180.0;
        double closingCos = Math.Cos(offLineRad);
        return (closingCos > 0.0) && (bar.ToStopFt <= (lookAheadFt * closingCos));
    }

    /// <summary>
    /// How far ahead (ft) a bar is looked for: far enough to brake from the current speed at the category's taxi brake
    /// rate to a stop half a fuselage short of the bar, with one tick of travel on top, and never less than
    /// <see cref="HoldShortDetectionNm"/>.
    /// </summary>
    private static double BarLookAheadFt(PhaseContext ctx, double halfLengthFt)
    {
        double speedKts = ctx.Aircraft.GroundSpeed;
        double brakingFt = (speedKts * speedKts) / (2.0 * CategoryPerformance.TaxiDecelRate(ctx.Category)) * GroundStopBraking.FeetPerSecondPerKt;
        double oneTickFt = speedKts * GroundStopBraking.FeetPerSecondPerKt * ctx.DeltaSeconds;
        double stopFt = halfLengthFt + GroundNavigator.SetBackStopMarginFt + brakingFt + oneTickFt;
        return Math.Max(HoldShortDetectionNm * GeoMath.FeetPerNm, stopFt);
    }

    /// <summary>
    /// A runway hold-short bar within <paramref name="lookAheadFt"/> and off the nose. Inside
    /// <see cref="HoldShortDetectionNm"/> any bar within <see cref="HoldShortAngleThreshold"/> of the nose counts; beyond
    /// it, only a bar within <see cref="BarCorridorHalfWidthFt"/> of the line the follower is rolling along, so the longer
    /// look-ahead a fast follower needs does not stop it at a bar on a connector beside its taxiway.
    /// </summary>
    private static bool IsBarAhead(PhaseContext ctx, GroundNode node, double lookAheadFt)
    {
        if (node.Type != GroundNodeType.RunwayHoldShort)
        {
            return false;
        }

        double distFt = GeoMath.DistanceNm(ctx.Aircraft.Position, node.Position) * GeoMath.FeetPerNm;
        if (distFt > lookAheadFt)
        {
            return false;
        }

        double bearing = GeoMath.BearingTo(ctx.Aircraft.Position, node.Position);
        double offNoseDeg = Math.Abs(GeoMath.SignedBearingDifference(ctx.Aircraft.TrueHeading.Degrees, bearing));
        if (offNoseDeg > HoldShortAngleThreshold)
        {
            return false;
        }

        double offLineFt = distFt * Math.Sin(offNoseDeg * Math.PI / 180.0);
        return (distFt <= (HoldShortDetectionNm * GeoMath.FeetPerNm)) || (offLineFt <= BarCorridorHalfWidthFt);
    }

    /// <summary>
    /// Take the hold at <paramref name="bar"/> once the follower is within one sub-tick of braking of rest — so clearing the
    /// residual is no more than physics' own ground snap would — and within <see cref="BarStopTakeFt"/> of the hold line
    /// (or past it). On the <see cref="GroundStopBraking.StopBraking.Backstop"/>, take it the tick before the nose would
    /// reach the line even braking at the max-effort rate, stopping dead where the follower is, and log that one-tick stop as a
    /// warning. Inserts <see cref="HoldingShortPhase"/> and a fresh follow behind it. Returns true when the hold was taken.
    /// </summary>
    private bool TryTakeHoldAtBar(PhaseContext ctx, BarAhead bar, GroundStopBraking.StopBraking braking)
    {
        double residualKts = CategoryPerformance.TaxiDecelRate(ctx.Category) / SimulationEngine.PhysicsSubTickRate;
        bool settled = (ctx.Aircraft.IndicatedAirspeed <= residualKts) && (bar.ToStopFt <= BarStopTakeFt);
        bool lastResort =
            (braking == GroundStopBraking.StopBraking.Backstop)
            && (bar.ToStopFt <= (GroundStopBraking.MaxEffortBrakingTravelThisTickFt(ctx) + BarStopTakeFt));
        if (!settled && !lastResort)
        {
            return false;
        }

        GroundNode node = bar.Node;
        if (lastResort)
        {
            Log.LogWarning(
                "[Follow] {Callsign}: stopped dead at runway node {NodeId} ({Runway}), reason={Reason}, nose {ToStop:F1} ft from the hold line "
                    + "at {Speed:F1} kt: no brake rate made the line",
                ctx.Aircraft.Callsign,
                node.Id,
                node.RunwayId?.ToString() ?? "unknown",
                bar.Reason,
                bar.ToStopFt,
                ctx.Aircraft.GroundSpeed
            );
        }
        else
        {
            Log.LogDebug(
                "[Follow] {Callsign}: holding short at runway node {NodeId} ({Runway}), reason={Reason}, nose {ToStop:F1} ft from the hold line, "
                    + "{Speed:F1} kt at rest",
                ctx.Aircraft.Callsign,
                node.Id,
                node.RunwayId?.ToString() ?? "unknown",
                bar.Reason,
                bar.ToStopFt,
                ctx.Aircraft.GroundSpeed
            );
        }
        ctx.Aircraft.IndicatedAirspeed = 0;
        ctx.Targets.TargetSpeed = 0;
        ctx.Targets.DesiredDecelRate = null;
        ctx.Aircraft.Ground.CurrentTaxiway = TaxiwayAtBar(node, ctx.Aircraft.Position);
        ReleaseLatchedBar();

        var holdShort = new HoldShortPoint
        {
            NodeId = node.Id,
            Reason = bar.Reason,
            TargetName = node.RunwayId?.ToString(),
        };

        var holdPhase = new HoldingShortPhase(holdShort);
        var resumeFollow = new FollowingPhase(_targetCallsign);
        ctx.Aircraft.Phases?.InsertAfterCurrent([holdPhase, resumeFollow]);
        return true;
    }

    /// <summary>
    /// The taxiway the follower holds on: the name of the bar node's taxiway edge that runs back toward the aircraft, or
    /// null when no edge there is named, so the hold-short report falls back to "taxiway" rather than naming a stale one.
    /// A follow has no route to read it from, and the hold-short report names it.
    /// </summary>
    private static string? TaxiwayAtBar(GroundNode bar, LatLon aircraftPosition)
    {
        double towardAircraft = GeoMath.BearingTo(bar.Position, aircraftPosition);
        string? nearest = null;
        double nearestOffDeg = double.MaxValue;
        foreach (IGroundEdge edge in bar.Edges)
        {
            if (edge.IsRunwayCenterline || string.IsNullOrEmpty(edge.TaxiwayName))
            {
                continue;
            }

            double offDeg = Math.Abs(GeoMath.SignedBearingDifference(towardAircraft, GeoMath.BearingTo(bar.Position, FarNode(edge, bar).Position)));
            if (offDeg < nearestOffDeg)
            {
                nearestOffDeg = offDeg;
                nearest = edge.TaxiwayName;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Whether the aircraft is already on the pavement of the runway this bar protects. A follower being led
    /// off a runway it was staged on — down 1R and out via the F1 intersection — passes that runway's far-side
    /// bar on the way out, and stopping there would leave it holding short of the runway it is standing on.
    /// Leaving a runway is not crossing it; only a bar for a runway the aircraft is not yet on is a crossing.
    /// </summary>
    private static bool IsAlreadyOnThatRunway(PhaseContext ctx, GroundNode node)
    {
        if ((node.RunwayId is not { } barRunway) || ctx.GroundLayout is null)
        {
            return false;
        }

        foreach (RunwayInfo runway in RunwayOccupancy.AirportRunways(ctx.GroundLayout.AirportId))
        {
            if (runway.Id.Overlaps(barRunway) && RunwayOccupancy.IsOnPavement(ctx.Aircraft, runway))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A follow has no route to name the taxiway it is on, so a moving follower reads it from the straight taxiway edge it
    /// is rolling on — null off every taxiway or on an unnamed one — rather than carry a name from before, such as the
    /// taxiway a previous hold was on. The edge is looked for first where the follower last was
    /// (<see cref="_taxiEdgeNodeIds"/>), through <see cref="TaxiEdgeLocator.EdgeUnder"/>.
    /// </summary>
    private void RefreshCurrentTaxiway(PhaseContext ctx)
    {
        if ((ctx.GroundLayout is null) || (ctx.Aircraft.GroundSpeed <= 0.0))
        {
            return;
        }

        GroundEdge? onEdge = TaxiEdgeLocator.EdgeUnder(ctx.GroundLayout, ctx.Aircraft.Position, _taxiEdgeNodeIds);
        _taxiEdgeNodeIds = onEdge is null ? null : (onEdge.Nodes[0].Id, onEdge.Nodes[1].Id);
        ctx.Aircraft.Ground.CurrentTaxiway = onEdge?.TaxiwayName is { Length: > 0 } taxiway ? taxiway : null;
    }

    /// <summary>Whether the bar protects a runway the crossing clearance that started this follow already cleared.</summary>
    private bool IsClearedToCross(GroundNode node) =>
        (node.RunwayId is { } barRunway) && _crossingClearedRunways.Any(cleared => barRunway.Overlaps(cleared));

    /// <summary>
    /// Spend the crossing clearance once it has been used: note when the aircraft is on the pavement of a cleared
    /// runway, and drop the clearance the first tick after that it is clear of all of them, so a bar of the same
    /// runway met later in the follow stops the aircraft again.
    /// </summary>
    private void ExpireUsedCrossingClearance(PhaseContext ctx)
    {
        if ((_crossingClearedRunways.Count == 0) || ctx.GroundLayout is null)
        {
            return;
        }

        bool onClearedRunway = RunwayOccupancy
            .AirportRunways(ctx.GroundLayout.AirportId)
            .Any(runway => _crossingClearedRunways.Any(cleared => runway.Id.Overlaps(cleared)) && RunwayOccupancy.IsOnPavement(ctx.Aircraft, runway));
        if (onClearedRunway)
        {
            _hasBeenOnClearedRunway = true;
            return;
        }

        if (_hasBeenOnClearedRunway)
        {
            Log.LogDebug("[Follow] {Callsign}: clear of the runways its crossing clearance covered; clearance spent", ctx.Aircraft.Callsign);
            _crossingClearedRunways = [];
            _hasBeenOnClearedRunway = false;
        }
    }

    /// <summary>Whether the crossing clearance has been used on a cleared runway and not yet spent (snapshotted).</summary>
    public bool HasBeenOnClearedRunway => _hasBeenOnClearedRunway;

    /// <summary>
    /// Whether this bar is the one the follower's own taxi clearance ends at. FOLLOWG does not erase the route
    /// the aircraft was cleared on, so a bar matching that route's destination runway is its departure bar, not
    /// a crossing. The distinction is load-bearing twice over: <see cref="HoldShortReason.DestinationRunway"/>
    /// makes <c>RES</c> refuse to release it onto the runway without a takeoff clearance, and it puts the
    /// aircraft at tier 0 in <see cref="RunwayDepartureQueue"/> — at the front of the line, where it is.
    /// </summary>
    private static bool BarIsOwnDestination(PhaseContext ctx, GroundNode node)
    {
        if (node.RunwayId is not { } barRunway)
        {
            return false;
        }

        HoldShortPoint? destination = ctx.Aircraft.Ground.AssignedTaxiRoute?.HoldShortPoints.FirstOrDefault(h =>
            h.Reason == HoldShortReason.DestinationRunway
        );
        return (destination?.TargetName is { } name) && barRunway.Overlaps(RunwayIdentifier.Parse(name));
    }
}
