using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft crosses a runway at runway-crossing speed by following the taxi
/// line via a <see cref="GroundNavigator"/> over the crossing slice of the
/// aircraft's <see cref="TaxiRoute"/>. Each tick steers via the navigator
/// (which respects arcs, fillets and intermediate runway-centerline nodes
/// that the painted line traverses), then completes when the navigator
/// reaches a virtual node offset ½ aircraft length past the exit-side
/// hold-short.
///
/// Earlier versions used a single straight-line beeline from the entry-side
/// hold-short to the exit-side hold-short, which cut diagonally across the
/// runway surface whenever the taxiway crossed via a fillet arc rather than
/// an exactly perpendicular straight line (e.g. SFO H crossing 01L/19R —
/// see GitHub issue #166).
/// </summary>
public sealed class CrossingRunwayPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("CrossingRunwayPhase");

    private const double LogIntervalSeconds = 3.0;

    private readonly int _approachNodeId;
    private readonly int _targetNodeId;
    private readonly string? _runwayId;

    // Built lazily in OnStart (or first OnTick after snapshot restore) by
    // slicing the aircraft's AssignedTaxiRoute between approach and target.
    private TaxiRoute? _crossingRoute;
    private IGroundNavigator? _navigator;
    private bool _initialized;
    private double _timeSinceLastLog;

    public CrossingRunwayPhase(int approachNodeId, int targetNodeId, string? runwayId)
    {
        _approachNodeId = approachNodeId;
        _targetNodeId = targetNodeId;
        _runwayId = runwayId;
    }

    public override string Name => "Crossing Runway";

    public string? RunwayId => _runwayId;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetSpeed = CategoryPerformance.RunwayCrossingSpeed(ctx.Category);

        TryBuildCrossingRoute(ctx);

        Log.LogDebug(
            "[Crossing] {Callsign}: crossing runway {Rwy}, approach={Approach}, target={Target}, initialized={Init}",
            ctx.Aircraft.Callsign,
            _runwayId ?? "?",
            _approachNodeId,
            _targetNodeId,
            _initialized
        );
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_initialized)
        {
            TryBuildCrossingRoute(ctx);
        }

        if (ctx.Aircraft.Ground.IsImmobile)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            return false;
        }

        if (!_initialized || _navigator is null || _crossingRoute is null)
        {
            // Degenerate fallback: no route slice available. Stop where we are
            // so the next phase can take over (or be inserted) without driving
            // the aircraft anywhere by guesswork. Should only happen if the
            // phase is constructed in a test without an AssignedTaxiRoute.
            ctx.Targets.TargetSpeed = 0;
            return true;
        }

        bool isLastSegment = _crossingRoute.CurrentSegmentIndex + 1 >= _crossingRoute.Segments.Count;
        var result = _navigator.Tick(ctx, isLastSegment, _ => true);

        if (result == NavigatorResult.ArrivedAtNode)
        {
            if (_crossingRoute.CurrentSegment is { } seg)
            {
                ctx.Aircraft.Ground.CurrentTaxiway = seg.TaxiwayName;
            }

            int extraAdvance = _navigator.ExtraSegmentsToAdvance;
            _crossingRoute.CurrentSegmentIndex += 1 + extraAdvance;
            if (_crossingRoute.CurrentSegmentIndex > _crossingRoute.Segments.Count)
            {
                _crossingRoute.CurrentSegmentIndex = _crossingRoute.Segments.Count;
            }

            if (_crossingRoute.IsComplete)
            {
                return true;
            }

            _navigator.SetupSegment(_crossingRoute, ctx, _ => true);
        }

        _timeSinceLastLog += ctx.DeltaSeconds;
        if (_timeSinceLastLog >= LogIntervalSeconds)
        {
            _timeSinceLastLog = 0;
            Log.LogTrace(
                "[Crossing] {Callsign}: seg={Idx}/{Count} gs={Gs:F1}kts",
                ctx.Aircraft.Callsign,
                _crossingRoute.CurrentSegmentIndex,
                _crossingRoute.Segments.Count,
                ctx.Aircraft.GroundSpeed
            );
        }

        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        Log.LogDebug("[Crossing] {Callsign}: OnEnd ({Status})", ctx.Aircraft.Callsign, endStatus);
        // Speed targets are owned by the next phase. The typical successor is
        // TaxiingPhase (TaxiingPhase.cs BuildResumePhases) — zeroing IAS here
        // would force a stop the aircraft has to re-accelerate from. If the
        // route ends after the crossing, the inserted HoldingInPositionPhase
        // / AtParkingPhase will brake to zero on its own.
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        return cmd switch
        {
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is crossing a runway; only HOLD or a new TAXI apply until the crossing completes"),
        };
    }

    /// <summary>
    /// Slice <see cref="AircraftGroundState.AssignedTaxiRoute"/> between the
    /// entry- and exit-side hold-short nodes, append a virtual tail-clearance
    /// node ½ aircraft length past the exit, and hand the result to a new
    /// <see cref="GroundNavigator"/>. Idempotent — only builds the navigator
    /// once per phase instance.
    /// </summary>
    private void TryBuildCrossingRoute(PhaseContext ctx)
    {
        if (_initialized || ctx.GroundLayout is null)
        {
            return;
        }

        var route = ctx.Aircraft.Ground.AssignedTaxiRoute;
        if (route is null || route.Segments.Count == 0)
        {
            return;
        }

        // Find the slice [entryIdx..exitIdx] in the existing route:
        //   entryIdx: first segment whose FromNodeId == _approachNodeId
        //   exitIdx:  first segment at or after entryIdx whose ToNodeId == _targetNodeId
        int entryIdx = -1;
        int exitIdx = -1;
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];
            if (entryIdx < 0 && seg.FromNodeId == _approachNodeId)
            {
                entryIdx = i;
            }
            if (entryIdx >= 0 && seg.ToNodeId == _targetNodeId)
            {
                exitIdx = i;
                break;
            }
        }

        if (entryIdx < 0 || exitIdx < 0)
        {
            Log.LogWarning(
                "[Crossing] {Callsign}: could not slice crossing segments from route (approach={Approach}, target={Target}, segments={Count})",
                ctx.Aircraft.Callsign,
                _approachNodeId,
                _targetNodeId,
                route.Segments.Count
            );
            return;
        }

        var slice = new List<TaxiRouteSegment>(capacity: exitIdx - entryIdx + 2);
        for (int i = entryIdx; i <= exitIdx; i++)
        {
            slice.Add(route.Segments[i]);
        }

        // Append a virtual past-target segment for tail clearance (½ aircraft length).
        if (ctx.GroundLayout.Nodes.TryGetValue(_targetNodeId, out var targetNode))
        {
            double lengthFt = FaaAircraftDatabase.Get(ctx.Aircraft.AircraftType)?.LengthFt ?? 60.0;
            double halfLengthNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;

            GroundNode? approachForOffset = null;
            if (ctx.GroundLayout.Nodes.TryGetValue(slice[^1].FromNodeId, out var prevNode))
            {
                approachForOffset = prevNode;
            }
            else if (ctx.GroundLayout.Nodes.TryGetValue(_approachNodeId, out var apNode))
            {
                approachForOffset = apNode;
            }

            if (approachForOffset is not null)
            {
                var virtualTarget = VirtualNode.OffsetPast(ctx.GroundLayout, targetNode, approachForOffset, halfLengthNm);
                slice.Add(VirtualNode.CreateSegment(targetNode, virtualTarget, slice[^1].TaxiwayName));
            }
        }

        _crossingRoute = new TaxiRoute { Segments = slice, HoldShortPoints = [] };

        // Cross and continue without delay (7110.65 §3-7-2): floor the speed at the crossing speed so the
        // navigator never brakes toward a stop on the runway or at the (artificial) slice end, and the
        // off-centerline re-acquire gate can't cap it mid-crossing. The aircraft hands off to the onward
        // TaxiingPhase still moving; that phase owns the real deceleration for the destination. The floor
        // never overrides a conflict/airport speed-limit ceiling (see ClampBySpeedLimit).
        double maxSpeed = CategoryPerformance.RunwayCrossingSpeed(ctx.Category);
        _navigator = GroundNavigatorRouter.Create();
        _navigator.MaxSpeedKts = maxSpeed;
        _navigator.MinSpeedKts = maxSpeed;
        _navigator.SetupSegment(_crossingRoute, ctx, _ => true);

        _initialized = true;

        Log.LogDebug(
            "[Crossing] {Callsign}: crossing route built, {SegCount} segments, maxSpeed={Speed:F0}kts",
            ctx.Aircraft.Callsign,
            slice.Count,
            maxSpeed
        );
    }

    public override PhaseDto ToSnapshot() =>
        new CrossingRunwayPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            ApproachNodeId = _approachNodeId,
            TargetNodeId = _targetNodeId,
            CrossingRunwayId = _runwayId,
            Initialized = _initialized,
            TimeSinceLastLog = _timeSinceLastLog,
            Navigator = _navigator?.ToSnapshot(),
            CrossingRouteSegmentIndex = _crossingRoute?.CurrentSegmentIndex ?? 0,
        };

    public static CrossingRunwayPhase FromSnapshot(CrossingRunwayPhaseDto dto)
    {
        var phase = new CrossingRunwayPhase(dto.ApproachNodeId, dto.TargetNodeId, dto.CrossingRunwayId);
        phase._timeSinceLastLog = dto.TimeSinceLastLog;
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        // Leave _initialized=false so the first OnTick rebuilds the
        // navigator + route slice from the restored AssignedTaxiRoute.
        // dto.Navigator / dto.CrossingRouteSegmentIndex are forward-compat
        // placeholders; the rebuilt slice is canonical because the route
        // (and the airport layout) are what FromSnapshot can actually
        // resolve at restore time.
        _ = dto.Initialized;
        _ = dto.Navigator;
        _ = dto.CrossingRouteSegmentIndex;
        return phase;
    }
}
