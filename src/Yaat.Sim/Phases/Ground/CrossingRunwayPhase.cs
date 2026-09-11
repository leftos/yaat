using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Aircraft crosses a runway at normal taxi speed (runway-crossing speed kept only
/// as a no-stop floor) by following the taxi line via a <see cref="GroundNavigator"/>
/// over the crossing slice of the aircraft's <see cref="TaxiRoute"/>. Each tick steers
/// via the navigator (which respects arcs, fillets and intermediate runway-centerline
/// nodes that the painted line traverses, and slows for any curve via its arc-speed
/// cap), then completes ½ aircraft length past the exit-side hold-short, following
/// the route's own onward segments to get there so the tail clears the runway without
/// leaving the painted line. Crossing without delay (7110.65 §3-7-2.a.10, "CROSS
/// (runway) … WITHOUT DELAY"; AIM 4-3-21.a) minimizes runway occupancy, so the aircraft
/// does not slow below its taxi speed for a straight crossing.
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
    private GroundNavigator? _navigator;
    private bool _initialized;
    private double _timeSinceLastLog;

    // Where the slice sits in the aircraft's own route, so the crossing can hand the route back at the
    // segment the aircraft is standing on. All three are recomputed by TryBuildCrossingRoute from the
    // restored route, so none of them is snapshotted (see FromSnapshot).
    private int _exitRouteIndex = -1;
    private int _tailClearFullSegments;
    private bool _tailClearPartial;

    public CrossingRunwayPhase(int approachNodeId, int targetNodeId, string? runwayId)
    {
        _approachNodeId = approachNodeId;
        _targetNodeId = targetNodeId;
        _runwayId = runwayId;
    }

    public override string Name => "Crossing Runway";

    public string? RunwayId => _runwayId;

    /// <summary>The crossing slice the navigator is playing back (entry bar → exit bar → tail-clearance). Null until built.</summary>
    internal TaxiRoute? CrossingRoute => _crossingRoute;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);

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
            // Pin the target too: a stale nonzero TargetSpeed left by the navigator would let
            // generic physics creep the aircraft forward each sub-tick between the IAS resets.
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        if (!_initialized || _navigator is null || _crossingRoute is null)
        {
            // Degenerate fallback: no route slice available. Stop where we are
            // so the next phase can take over (or be inserted) without driving
            // the aircraft anywhere by guesswork. Should only happen if the
            // phase is constructed in a test without an AssignedTaxiRoute.
            ctx.Targets.TargetSpeed = 0;
            HandRouteBack(ctx);
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

            _crossingRoute.CurrentSegmentIndex += 1;
            if (_crossingRoute.CurrentSegmentIndex > _crossingRoute.Segments.Count)
            {
                _crossingRoute.CurrentSegmentIndex = _crossingRoute.Segments.Count;
            }

            if (_crossingRoute.IsComplete)
            {
                HandRouteBack(ctx);
                return true;
            }

            _navigator.SetupSegment(_crossingRoute, ctx, _ => true);
            ApplyExitHoldShortOffset(ctx);
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
    /// entry- and exit-side hold-short nodes, extend it ½ aircraft length past
    /// the exit along the route's own onward segments for tail clearance, and
    /// hand the result to a new <see cref="GroundNavigator"/>. Idempotent —
    /// only builds the navigator once per phase instance.
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

        if (BuildSlice(ctx, route, _approachNodeId, _targetNodeId) is not { } plan)
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

        _exitRouteIndex = plan.ExitIndex;
        _tailClearFullSegments = plan.FullTailSegments;
        _tailClearPartial = plan.PartialTail;
        _crossingRoute = new TaxiRoute { Segments = plan.Segments, HoldShortPoints = [] };

        // Cross at normal taxi speed and continue without delay (7110.65 §3-7-2.a.10, "CROSS (runway) at
        // (taxiway) WITHOUT DELAY"; AIM 4-3-21.a): a crossing is just taxiing across, so the cap is the
        // category taxi speed — not a slower "crossing speed". RunwayCrossingSpeed is kept only as a navigator
        // FLOOR so the aircraft never brakes toward a stop on the runway or at the (artificial) slice end, and
        // the off-centerline re-acquire gate can't cap it mid-crossing. Both the arc/turn-speed cap for a curve
        // in the painted line and a conflict/airport speed-limit ceiling still outrank that floor (see
        // GroundNavigator.ClampBySpeedLimit). The aircraft hands off to the onward TaxiingPhase moving; that
        // phase owns the real deceleration for the destination.
        _navigator = new GroundNavigator();
        _navigator.MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category);
        _navigator.MinSpeedKts = CategoryPerformance.RunwayCrossingSpeed(ctx.Category);
        _navigator.SetupSegment(_crossingRoute, ctx, _ => true);
        ApplyExitHoldShortOffset(ctx);

        _initialized = true;

        Log.LogDebug(
            "[Crossing] {Callsign}: crossing route built, {SegCount} segments, exitRouteIdx={ExitIdx}, "
                + "tailClear={TailFull} full segment(s){Partial}, maxSpeed={Max:F0}kts floor={Floor:F0}kts",
            ctx.Aircraft.Callsign,
            plan.Segments.Count,
            _exitRouteIndex,
            _tailClearFullSegments,
            _tailClearPartial ? " + a partial cut" : "",
            _navigator.MaxSpeedKts,
            _navigator.MinSpeedKts
        );
    }

    /// <summary>
    /// The crossing slice and where it sits in the aircraft's own route: the segments from the entry-side
    /// hold-short through the exit-side one plus the tail-clearance extension, the route index of the exit
    /// segment, how many onward route segments the tail-clearance consumed whole, and whether it ends on a
    /// partial (virtual) cut inside the next one.
    /// </summary>
    private sealed record CrossingSlice(List<TaxiRouteSegment> Segments, int ExitIndex, int FullTailSegments, bool PartialTail);

    /// <summary>
    /// Build the crossing slice out of <paramref name="route"/>. Null when the route does not contain the
    /// crossing (no segment leaves <paramref name="approachNodeId"/>, or none reaches
    /// <paramref name="targetNodeId"/> after it). A pure function of the route, the layout and the
    /// aircraft's length — which is why nothing about the slice is snapshotted.
    /// </summary>
    private static CrossingSlice? BuildSlice(PhaseContext ctx, TaxiRoute route, int approachNodeId, int targetNodeId)
    {
        // Find the slice [entryIdx..exitIdx] in the existing route:
        //   entryIdx: first segment whose FromNodeId == approachNodeId
        //   exitIdx:  first segment at or after entryIdx whose ToNodeId == targetNodeId
        int entryIdx = -1;
        int exitIdx = -1;
        for (int i = 0; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];
            if (entryIdx < 0 && seg.FromNodeId == approachNodeId)
            {
                entryIdx = i;
            }
            if (entryIdx >= 0 && seg.ToNodeId == targetNodeId)
            {
                exitIdx = i;
                break;
            }
        }

        if (entryIdx < 0 || exitIdx < 0)
        {
            return null;
        }

        var slice = new List<TaxiRouteSegment>(capacity: exitIdx - entryIdx + 3);
        for (int i = entryIdx; i <= exitIdx; i++)
        {
            slice.Add(route.Segments[i]);
        }

        var (fullTailSegments, partialTail) = AppendTailClearance(ctx, route, exitIdx, slice);
        return new CrossingSlice(slice, exitIdx, fullTailSegments, partialTail);
    }

    /// <summary>
    /// Extend <paramref name="slice"/> ½ an aircraft length past the exit node so the tail clears the
    /// runway, by walking the route's OWN segments after <paramref name="exitIdx"/> — never the graph's
    /// straightest continuation past the exit node, which leaves the painted line wherever the route turns
    /// right after the crossing (SFO G → B across 01L/19R: a 121 ft straight extension along G ran ~55 ft
    /// wide of the fillet the aircraft was about to fly). A straight segment the ½-length boundary falls
    /// inside is cut at the exact distance with a virtual node; an arc (or a straight carrying shape points)
    /// is taken whole, because a free-space cut across a curve puts the aircraft off the centerline again
    /// and the short overshoot is harmless. The ½ length is what it takes for the tail to cross the holding
    /// position marking: an aircraft is not clear of the runway until every part of it has crossed that marking
    /// (AIM 2-3-5.a.1, AIM 4-3-21.b). Returns how many route segments were consumed whole and whether
    /// the extension ends on a cut.
    /// </summary>
    private static (int FullSegments, bool Partial) AppendTailClearance(PhaseContext ctx, TaxiRoute route, int exitIdx, List<TaxiRouteSegment> slice)
    {
        double lengthFt = FaaAircraftDatabase.Get(ctx.Aircraft.AircraftType)?.LengthFt ?? 60.0;
        if (TailClearanceSuppressed(ctx, route, exitIdx, lengthFt))
        {
            return (0, false);
        }

        int fullSegments = 0;
        double remainingNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;
        for (int i = exitIdx + 1; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];
            if (seg.Edge.DistanceNm <= remainingNm)
            {
                slice.Add(seg);
                fullSegments++;
                remainingNm -= seg.Edge.DistanceNm;
                continue;
            }

            if (seg.Edge.Edge is GroundEdge { IntermediatePoints.Count: 0 })
            {
                var from = seg.Edge.FromNode;
                var (lat, lon) = GeoMath.ProjectPointRaw(from.Position.Lat, from.Position.Lon, seg.Edge.DepartureBearing, remainingNm);
                slice.Add(VirtualNode.CreateSegment(from, VirtualNode.Create(lat, lon), seg.TaxiwayName));
                return (fullSegments, true);
            }

            slice.Add(seg);
            return (fullSegments + 1, false);
        }

        // The route ends inside the tail-clearance distance: stop at its end rather than driving past it.
        return (fullSegments, false);
    }

    /// <summary>
    /// Whether the ½-length tail-clearance extension must be suppressed, so the crossing ends at the exit
    /// node itself. Two cases, both hold-short compliance outranking tail clearance:
    /// a binding hold-short within a fuselage length past the exit (the aircraft cannot fit between the runway
    /// and that line — issue #172; overshooting would carry it past the hold line and force a ~180° reversal
    /// back to it), and an exit node that is itself a binding hold-short (see
    /// <see cref="ExitIsBindingHoldShort"/>).
    /// </summary>
    private static bool TailClearanceSuppressed(PhaseContext ctx, TaxiRoute route, int exitIdx, double lengthFt)
    {
        if (ctx.GroundLayout is null || !ctx.GroundLayout.Nodes.ContainsKey(route.Segments[exitIdx].ToNodeId))
        {
            return true;
        }

        return BindingHoldShortWithin(route, exitIdx, lengthFt / GeoMath.FeetPerNm, ctx.GroundLayout)
            || ExitIsBindingHoldShort(route, route.Segments[exitIdx].ToNodeId);
    }

    /// <summary>
    /// The index <see cref="TaxiRoute.CurrentSegmentIndex"/> will hold once the crossing from
    /// <paramref name="approachNodeId"/> to <paramref name="targetNodeId"/> completes — the first segment
    /// the tail-clearance extension does not finish. <see cref="TaxiingPhase"/>'s builders read this to
    /// decide whether a <see cref="TaxiingPhase"/> follows the crossing <i>without</i> moving the cursor;
    /// the crossing phase is the only writer (see <see cref="HandRouteBack"/>). Falls back to the current
    /// cursor when the route does not contain the crossing, so an unsliceable crossing still resumes taxiing.
    /// </summary>
    internal static int RouteIndexAfterCrossing(PhaseContext ctx, TaxiRoute route, int approachNodeId, int targetNodeId)
    {
        if (BuildSlice(ctx, route, approachNodeId, targetNodeId) is not { } plan)
        {
            return route.CurrentSegmentIndex;
        }

        return Math.Min(plan.ExitIndex + 1 + plan.FullTailSegments, route.Segments.Count);
    }

    /// <summary>
    /// Hand the aircraft's own route back at the segment it is standing on when the crossing ends: the exit
    /// segment plus every onward segment the tail-clearance extension drove to the end of. With a partial
    /// cut that is the segment the aircraft is standing on mid-way, which the navigator's I8 entry capture
    /// resumes from the live position. This is the ONLY write of
    /// <see cref="TaxiRoute.CurrentSegmentIndex"/> for a runway crossing — <see cref="TaxiingPhase"/>'s
    /// builders advance the cursor only past the segment the aircraft arrived at the end of, and never walk
    /// it across the crossing slice, so the cursor can no longer point at a segment already behind the nose.
    /// </summary>
    private void HandRouteBack(PhaseContext ctx)
    {
        var route = ctx.Aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return;
        }

        int exitIdx = _exitRouteIndex;
        if (exitIdx < 0)
        {
            // The slice never built (no layout, or a route that does not contain the crossing). Fall back to
            // the exit segment found by node id alone, so the onward taxi still starts past the runway.
            exitIdx = route.Segments.FindIndex(s => s.ToNodeId == _targetNodeId);
        }

        if (exitIdx < 0)
        {
            return;
        }

        route.CurrentSegmentIndex = Math.Min(exitIdx + 1 + _tailClearFullSegments, route.Segments.Count);
    }

    /// <summary>
    /// Whether the crossing's own exit node carries a binding (uncleared) hold-short — the parallel-runway case
    /// where the far side of the runway being crossed <i>is</i> the next runway's hold line (SFO taxiway C: cross
    /// 10R/28L, hold short 28R). The ½-length tail-clearance overshoot must be suppressed there for the same
    /// reason it is for a binding taxiway hold-short: hold-short compliance outranks tail clearance, and
    /// overshooting puts the aircraft inside the holding position markings of a runway it has no clearance to
    /// enter (AIM 4-3-18.a.5). Tail-over-runway exposure is reported by <see cref="HoldShortPoint.TailOverRunwayNodeId"/>.
    /// </summary>
    private static bool ExitIsBindingHoldShort(TaxiRoute route, int targetNodeId) => route.GetHoldShortAt(targetNodeId) is { IsCleared: false };

    /// <summary>
    /// Snap the navigator's target to the exit hold-short's nose-at-line position once the crossing is aimed at
    /// it. <see cref="TaxiingPhase.SetupCurrentSegment"/> does the same on the ordinary arrival path; without it
    /// the crossing stops at the node itself, which sits on the painted bar rather than a fuselage short of it.
    /// </summary>
    private void ApplyExitHoldShortOffset(PhaseContext ctx)
    {
        if (_navigator is null || _navigator.TargetNodeId != _targetNodeId)
        {
            return;
        }

        var route = ctx.Aircraft.Ground.AssignedTaxiRoute;
        if (route is null)
        {
            return;
        }

        if (route.GetHoldShortAt(_targetNodeId) is { IsCleared: false, Latitude: { } lat, Longitude: { } lon })
        {
            _navigator.OverrideTargetPosition(lat, lon);
        }
    }

    /// <summary>
    /// Whether the route has a binding (uncleared) hold-short — taxiway or runway — within
    /// <paramref name="withinNm"/> downstream of the crossing exit (segment index <paramref name="exitIdx"/>).
    /// A hold-short this close means the aircraft cannot fit a full fuselage between the runway it just crossed
    /// and that line, so the ½-length tail-clearance overshoot must be suppressed. A runway holding position
    /// binds exactly as a taxiway one does: driving up to it, or taking the next segment whole, puts the nose
    /// inside the markings of a runway the aircraft has no clearance to enter (AIM 2-3-5.a.1, AIM 4-3-18.a.5).
    /// A cleared one — the next runway of a multi-runway crossing — does not bind, so that crossing proceeds.
    /// </summary>
    private static bool BindingHoldShortWithin(TaxiRoute route, int exitIdx, double withinNm, AirportGroundLayout layout)
    {
        double accumulated = 0;
        for (int i = exitIdx + 1; i < route.Segments.Count; i++)
        {
            var seg = route.Segments[i];
            if (!layout.Nodes.TryGetValue(seg.FromNodeId, out var from) || !layout.Nodes.TryGetValue(seg.ToNodeId, out var to))
            {
                break;
            }

            accumulated += GeoMath.DistanceNm(from.Position, to.Position);

            var hs = route.GetHoldShortAt(seg.ToNodeId);
            if (hs is not null && !hs.IsCleared && accumulated <= withinNm)
            {
                return true;
            }

            if (accumulated > withinNm)
            {
                break;
            }
        }

        return false;
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
        // resolve at restore time. The slice's route index and tail-clearance
        // counters are not snapshotted either: BuildSlice derives all three
        // from the restored route, the layout and the aircraft's length, so a
        // restore mid-crossing recomputes exactly what the live phase held.
        _ = dto.Initialized;
        _ = dto.Navigator;
        _ = dto.CrossingRouteSegmentIndex;
        return phase;
    }
}
