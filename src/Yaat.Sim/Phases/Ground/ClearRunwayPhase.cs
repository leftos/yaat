using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Pull-forward-to-clear-runway phase (issue #172 W5, the <c>CLRWY</c> command). An aircraft holding short
/// of a taxiway with its tail over a runway drives forward along its taxiway to a virtual node ½ aircraft
/// length past the runway's far hold-short bars — the same tail-clearance point a runway crossing with no
/// trailing hold-short stops at (the append that <c>CrossingRunwayPhase</c> suppresses when a binding taxiway
/// hold-short is in the way) — then completes into <see cref="HoldingInPositionPhase"/>, just clear of the
/// runway. The runway hold-short node and the approach (runway-side) node are kept so the route can be rebuilt
/// after a snapshot restore, and the snapshot carries the navigator, which resumes the primitive it was playing
/// (an entry-alignment turn part-way round), like <see cref="CrossingRunwayPhase"/>.
/// </summary>
public sealed class ClearRunwayPhase(int runwayNodeId, int approachNodeId) : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("ClearRunwayPhase");

    private readonly int _runwayNodeId = runwayNodeId;
    private readonly int _approachNodeId = approachNodeId;

    /// <summary>The runway node the aircraft pulls forward from: the runway it is clearing.</summary>
    public int RunwayNodeId => _runwayNodeId;

    private TaxiRoute? _route;
    private GroundNavigator? _navigator;
    private bool _initialized;

    /// <summary>The navigator a restored phase came back with, held until the first OnTick rebuilds the route on it.</summary>
    private GroundNavigatorDto? _restoredNavigator;

    public override string Name => "Clearing Runway";

    /// <summary>The one-segment clearance route (runway hold-short → ½ aircraft length past it). Null until built.</summary>
    public TaxiRoute? ClearanceRoute => _route;

    public override void OnStart(PhaseContext ctx)
    {
        ctx.Aircraft.IsOnGround = true;
        ctx.Targets.TargetSpeed = CategoryPerformance.TaxiSpeed(ctx.Category);
        BuildRoute(ctx);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_initialized)
        {
            BuildRoute(ctx);
        }

        if (ctx.Aircraft.Ground.IsImmobile)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            // Pin the target too: a stale nonzero TargetSpeed left by the navigator would let
            // generic physics creep the aircraft forward each sub-tick between the IAS resets.
            ctx.Targets.TargetSpeed = 0;
            return false;
        }

        if (!_initialized || _navigator is null || _route is null)
        {
            // Degenerate fallback: no clearance route could be built. Stop and let the next phase take
            // over rather than driving the aircraft anywhere by guesswork.
            ctx.Targets.TargetSpeed = 0;
            return true;
        }

        bool isLastSegment = _route.CurrentSegmentIndex + 1 >= _route.Segments.Count;
        NavigatorResult result = _navigator.Tick(ctx, isLastSegment, _ => true);
        if (result == NavigatorResult.ArrivedAtNode)
        {
            _route.CurrentSegmentIndex += 1;
            if (_route.IsComplete)
            {
                return true;
            }

            _navigator.SetupSegment(_route, ctx, _ => true);
        }

        return false;
    }

    private void BuildRoute(PhaseContext ctx)
    {
        if (_initialized || ctx.GroundLayout is null)
        {
            return;
        }

        AirportGroundLayout layout = ctx.GroundLayout;
        if (
            !layout.Nodes.TryGetValue(_runwayNodeId, out GroundNode? runwayNode)
            || !layout.Nodes.TryGetValue(_approachNodeId, out GroundNode? approachNode)
        )
        {
            Log.LogWarning(
                "[ClearRunway] {Callsign}: missing geometry (runway={Rwy}, approach={App}) — cannot build clearance route",
                ctx.Aircraft.Callsign,
                _runwayNodeId,
                _approachNodeId
            );

            // A restored navigator has no route to resume on; drop it so a later snapshot does not carry it on.
            _restoredNavigator = null;
            return;
        }

        double lengthFt = AircraftLength.ResolveFt(ctx.Aircraft.AircraftType);
        double halfLengthNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;

        // The clearance target: ½ aircraft length past the runway hold-short, away from the runway —
        // the tail just clears the bars (identical to the crossing tail-clearance offset).
        GroundNode target = VirtualNode.OffsetPast(layout, runwayNode, approachNode, halfLengthNm);
        TaxiRouteSegment segment = VirtualNode.CreateSegment(runwayNode, target, ctx.Aircraft.Ground.CurrentTaxiway ?? "");
        _route = new TaxiRoute { Segments = [segment], HoldShortPoints = [] };

        // A restored phase takes the navigator from the snapshot, so the set-up below resumes the primitive it was
        // playing at the progress it had reached (GroundNavigator.TryResumeRestoredPlayback).
        _navigator = _restoredNavigator is { } savedNavigator ? GroundNavigator.FromSnapshot(savedNavigator) : new GroundNavigator();
        _navigator.MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category);
        _restoredNavigator = null;
        _navigator.SetupSegment(_route, ctx, _ => true);
        _initialized = true;

        Log.LogDebug(
            "[ClearRunway] {Callsign}: pulling forward {Half:F0}ft past RWY node {Rwy}",
            ctx.Aircraft.Callsign,
            (lengthFt / 2.0),
            _runwayNodeId
        );
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd) =>
        cmd switch
        {
            CanonicalCommandType.HoldPosition => CommandAcceptance.Allowed,
            CanonicalCommandType.Taxi or CanonicalCommandType.TaxiAuto => CommandAcceptance.ClearsPhase,
            CanonicalCommandType.Delete => CommandAcceptance.ClearsPhase,
            _ => CommandAcceptance.Rejected("aircraft is pulling clear of the runway; only HOLD or a new TAXI apply until it stops"),
        };

    public override PhaseDto ToSnapshot() =>
        new ClearRunwayPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            RunwayNodeId = _runwayNodeId,
            ApproachNodeId = _approachNodeId,
            // A restored phase not yet ticked still holds the navigator it came back with.
            Navigator = _navigator?.ToSnapshot() ?? _restoredNavigator,
        };

    public static ClearRunwayPhase FromSnapshot(ClearRunwayPhaseDto dto)
    {
        var phase = new ClearRunwayPhase(dto.RunwayNodeId, dto.ApproachNodeId)
        {
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            _restoredNavigator = dto.Navigator,
        };
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
