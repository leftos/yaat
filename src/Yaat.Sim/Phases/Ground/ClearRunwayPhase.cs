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
/// runway. The runway hold-short node and the approach (runway-side) node are kept so the navigator can be
/// rebuilt after a snapshot restore; the navigator itself is transient, like <see cref="CrossingRunwayPhase"/>.
/// </summary>
public sealed class ClearRunwayPhase : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("ClearRunwayPhase");

    private readonly int _runwayNodeId;
    private readonly int _approachNodeId;

    private TaxiRoute? _route;
    private GroundNavigator? _navigator;
    private bool _initialized;

    public ClearRunwayPhase(int runwayNodeId, int approachNodeId)
    {
        _runwayNodeId = runwayNodeId;
        _approachNodeId = approachNodeId;
    }

    public override string Name => "Clearing Runway";

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
        var result = _navigator.Tick(ctx, isLastSegment, _ => true);
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

        var layout = ctx.GroundLayout;
        if (!layout.Nodes.TryGetValue(_runwayNodeId, out var runwayNode) || !layout.Nodes.TryGetValue(_approachNodeId, out var approachNode))
        {
            Log.LogWarning(
                "[ClearRunway] {Callsign}: missing geometry (runway={Rwy}, approach={App}) — cannot build clearance route",
                ctx.Aircraft.Callsign,
                _runwayNodeId,
                _approachNodeId
            );
            return;
        }

        double lengthFt = FaaAircraftDatabase.Get(ctx.Aircraft.AircraftType)?.LengthFt ?? 60.0;
        double halfLengthNm = (lengthFt / 2.0) / GeoMath.FeetPerNm;

        // The clearance target: ½ aircraft length past the runway hold-short, away from the runway —
        // the tail just clears the bars (identical to the crossing tail-clearance offset).
        var target = VirtualNode.OffsetPast(layout, runwayNode, approachNode, halfLengthNm);
        var segment = VirtualNode.CreateSegment(runwayNode, target, ctx.Aircraft.Ground.CurrentTaxiway ?? "");
        _route = new TaxiRoute { Segments = [segment], HoldShortPoints = [] };

        _navigator = new GroundNavigator();
        _navigator.MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category);
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
        };

    public static ClearRunwayPhase FromSnapshot(ClearRunwayPhaseDto dto)
    {
        var phase = new ClearRunwayPhase(dto.RunwayNodeId, dto.ApproachNodeId);
        phase.Status = (PhaseStatus)dto.Status;
        phase.ElapsedSeconds = dto.ElapsedSeconds;
        phase.RestoreRequirements(dto.Requirements);
        return phase;
    }
}
