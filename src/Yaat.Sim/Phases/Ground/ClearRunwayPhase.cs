using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Pilot;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// Pull-forward-to-clear-runway phase (issue #172 W5, the <c>CLRWY</c> command). An aircraft holding short
/// of a taxiway with its tail over a runway drives forward along its taxiway to a virtual node ½ aircraft
/// length plus <see cref="RunwayTailClearance.MarginFt"/> past the runway's far hold-short bars
/// (<see cref="RunwayTailClearance.PastBarFt"/>), so its tail stops that margin past the marking, then completes into
/// <see cref="HoldingInPositionPhase"/>, just clear of the runway. The stop is cut back so the nose stays at or short of another
/// runway's holding position marking (<see cref="RunwayTailClearance.StopPastBar"/>); when that leaves the tail inside this
/// runway's, the hold carries both runways (<see cref="HoldingInPositionPhase.HeldShortOfRunway"/>,
/// <see cref="HoldingInPositionPhase.TailInsideRunway"/>), the aircraft keeps this runway as its own and the pilot reports it is
/// not clear. An aircraft already at or past the stop holds where it is; when its nose is already past the other runway's
/// marking, the hold carries only the runway its tail is inside (no <see cref="HoldingInPositionPhase.HeldShortOfRunway"/>).
/// The runway hold-short node and the approach
/// (runway-side) node are kept so the route can be rebuilt after a snapshot restore, and the snapshot carries the
/// navigator, which resumes the primitive it was playing (an entry-alignment turn part-way round), like <see cref="CrossingRunwayPhase"/>.
/// </summary>
public sealed class ClearRunwayPhase(int runwayNodeId, int approachNodeId) : Phase
{
    private static readonly ILogger Log = SimLog.CreateLogger("ClearRunwayPhase");

    private readonly int _runwayNodeId = runwayNodeId;
    private readonly int _approachNodeId = approachNodeId;

    /// <summary>The runway node the aircraft pulls forward from: the runway it is clearing.</summary>
    public int RunwayNodeId => _runwayNodeId;

    private TaxiRoute? _route;
    private RunwayTailClearance.Stop? _stop;
    private GroundNavigator? _navigator;
    private bool _initialized;

    /// <summary>The aircraft is already at or past the stop, so it holds where it is rather than turning back to it.</summary>
    private bool _alreadyAtStop;

    /// <summary>The navigator a restored phase came back with, held until the first OnTick rebuilds the route on it.</summary>
    private GroundNavigatorDto? _restoredNavigator;

    public override string Name => "Clearing Runway";

    /// <summary>
    /// The one-segment clearance route (runway hold-short → <see cref="RunwayTailClearance.PastBarFt"/> past it). Null until built.
    /// </summary>
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

        if (_alreadyAtStop)
        {
            ctx.Aircraft.IndicatedAirspeed = 0;
            ctx.Targets.TargetSpeed = 0;
            HoldNotClearIfTailInside(ctx);
            return true;
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
                HoldNotClearIfTailInside(ctx);
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

        // The clearance target: ½ aircraft length plus the tail-clear margin past the runway hold-short, away from the
        // runway, so the tail stops the margin past the bars; cut back so the nose stays short of another runway's line ahead.
        _stop = RunwayTailClearance.StopPastBar(layout, runwayNode, approachNode, lengthFt);
        TaxiRouteSegment segment = VirtualNode.CreateSegment(runwayNode, _stop.Target, ctx.Aircraft.Ground.CurrentTaxiway ?? "");
        _route = new TaxiRoute { Segments = [segment], HoldShortPoints = [] };

        // A stop that clears the runway leaves the aircraft no runway of its own (GroundCommandHandler.TryClearRunway kept the
        // one it was clearing for the case it does not).
        if (!_stop.TailInsideLine && (ctx.Aircraft.Phases is { } phases))
        {
            phases.AssignedRunway = null;
        }

        _alreadyAtStop = IsAtOrPastStop(ctx.Aircraft, runwayNode, _stop);
        _initialized = true;
        Log.LogDebug(
            "[ClearRunway] {Callsign}: pulling forward {PastBar:F0}ft past RWY node {Rwy}, nose held short of bar {Bar}{AtStop}",
            ctx.Aircraft.Callsign,
            _stop.PastBarFt,
            _runwayNodeId,
            _stop.HeldShortOfBar?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(none)",
            _alreadyAtStop ? "; already there, holding" : ""
        );
        if (_alreadyAtStop)
        {
            _restoredNavigator = null;
            return;
        }

        // A restored phase takes the navigator from the snapshot, so the set-up below resumes the primitive it was
        // playing at the progress it had reached (GroundNavigator.TryResumeRestoredPlayback).
        _navigator = _restoredNavigator is { } savedNavigator ? GroundNavigator.FromSnapshot(savedNavigator) : new GroundNavigator();
        _navigator.MaxSpeedKts = CategoryPerformance.TaxiSpeed(ctx.Category);
        _restoredNavigator = null;
        _navigator.SetupSegment(_route, ctx, _ => true);
    }

    /// <summary>
    /// The aircraft is within the navigator's stop tolerance of <paramref name="stop"/>'s target, or beyond it, along the line from
    /// <paramref name="runwayNode"/> to the target.
    /// </summary>
    private static bool IsAtOrPastStop(AircraftState aircraft, GroundNode runwayNode, RunwayTailClearance.Stop stop)
    {
        double pastBarNm = stop.PastBarFt / GeoMath.FeetPerNm;
        if (pastBarNm <= GroundNavigator.FinalNodeArrivalThresholdNm)
        {
            return GeoMath.DistanceNm(aircraft.Position, runwayNode.Position) <= GroundNavigator.FinalNodeArrivalThresholdNm;
        }

        var bearing = new TrueHeading(GeoMath.BearingTo(runwayNode.Position, stop.Target.Position));
        double aheadOfAircraftNm = GeoMath.AlongTrackDistanceNm(stop.Target.Position, aircraft.Position, bearing);
        return aheadOfAircraftNm <= GroundNavigator.FinalNodeArrivalThresholdNm;
    }

    /// <summary>
    /// When the stop was cut back at another runway's line and the tail is still inside the runway being cleared, the aircraft
    /// has not cleared it: it holds in position short of the other runway with its tail inside this one
    /// (<see cref="HoldingInPositionPhase(string, string, IReadOnlyCollection{string})"/>), its assigned runway the one it is still on, and the pilot reports
    /// holding short of the other runway, not clear of this one; only not clear of this one when the nose is already past the
    /// other runway's marking.
    /// </summary>
    private void HoldNotClearIfTailInside(PhaseContext ctx)
    {
        if (
            (_stop is not { TailInsideLine: true, HeldShortOfBar: { } otherBar })
            || (ctx.GroundLayout is not { } layout)
            || (ctx.Aircraft.Phases is not { } phases)
            || !layout.Nodes.TryGetValue(_runwayNodeId, out GroundNode? runwayNode)
            || (runwayNode.RunwayId is not { } clearedRunway)
        )
        {
            return;
        }

        // The runway kept from before the CLRWY (GroundCommandHandler.TryClearRunway) is assigned and named by its own end. Any other
        // runway is assigned by the end nearest the aircraft (ActiveRunways.Empty): the assignment is sim state, not speech, so it
        // does not follow the room's active runways; only the spoken end below does.
        RunwayInfo? kept = ((phases.AssignedRunway is { } assigned) && assigned.Id.Overlaps(clearedRunway)) ? assigned : null;
        RunwayInfo? clearedInfo =
            kept
            ?? NavigationDatabase.InstanceOrNull?.GetRunway(
                layout.AirportId,
                RunwayCrossingEnd.Spoken(ctx.Aircraft, clearedRunway.ToString(), layout, ActiveRunways.Empty)
            );
        if (clearedInfo is null)
        {
            Log.LogWarning(
                "[ClearRunway] {Callsign}: no navdata for runway {Runway} at {Airport}; holding not clear of it with no runway assigned",
                ctx.Aircraft.Callsign,
                clearedRunway.ToString(),
                layout.AirportId
            );
        }

        phases.AssignedRunway = clearedInfo;
        string clearedEnd = RunwayIdentifier.ToDisplayDesignator(
            kept?.Designator ?? RunwayCrossingEnd.Spoken(ctx.Aircraft, clearedRunway.ToString(), layout, ctx.ActiveRunways)
        );

        // A nose already past the other runway's line before the CLRWY is not holding short of it: the hold says only that it is
        // not clear. A stop this phase drove to leaves the nose at the line.
        string? otherEnd =
            (_alreadyAtStop && IsNosePastBar(ctx.Aircraft, runwayNode, otherBar))
                ? null
                : RunwayTailClearance.HeldShortRunwayEnd(layout, otherBar, clearedInfo, ctx.Aircraft, ctx.ActiveRunways);
        Log.LogInformation(
            "[ClearRunway] {Callsign}: nose {Where} on {Twy}, tail still inside {Runway}'s line — holding, not clear",
            ctx.Aircraft.Callsign,
            otherEnd is null ? $"already past bar {otherBar.Id}" : $"at runway {otherEnd}'s line",
            ctx.Aircraft.Ground.CurrentTaxiway ?? "taxiway",
            clearedEnd
        );
        phases.ReplaceUpcoming([new HoldingInPositionPhase(otherEnd, clearedEnd, PilotResponder.SoloPositionsTower)]);
    }

    /// <summary>
    /// How far past another runway's bar a nose may read and still be at the cut-back stop: the stop aims the nose
    /// <see cref="GroundNavigator.SetBackStopMarginFt"/> short of the bar and the navigator arrives within
    /// <see cref="GroundNavigator.FinalNodeArrivalThresholdNm"/>; on a curved connector the straight-line reading of such a stop
    /// can fall past the bar.
    /// </summary>
    private const double NosePastBarSlackFt = GroundNavigator.SetBackStopMarginFt + (GroundNavigator.FinalNodeArrivalThresholdNm * GeoMath.FeetPerNm);

    /// <summary>
    /// The aircraft's nose, half its length ahead of its centre, lies more than <see cref="NosePastBarSlackFt"/> past
    /// <paramref name="otherBar"/> along the line from <paramref name="runwayNode"/>, the bar of the runway it is leaving, to that bar:
    /// it is past the other runway's holding position marking, not holding short of it. A <c>CLRWY</c> judges a nose that pulled up
    /// before it so, and a crossing that ends at rest judges where its nose came to rest
    /// (<see cref="CrossingRunwayPhase.EndAtRest"/>).
    /// </summary>
    public static bool IsNosePastBar(AircraftState aircraft, GroundNode runwayNode, GroundNode otherBar)
    {
        var bearing = new TrueHeading(GeoMath.BearingTo(runwayNode.Position, otherBar.Position));
        double barAheadFt = GeoMath.AlongTrackDistanceNm(otherBar.Position, aircraft.Position, bearing) * GeoMath.FeetPerNm;
        double nosePastBarFt = (AircraftLength.ResolveFt(aircraft.AircraftType) / 2.0) - barAheadFt;
        return nosePastBarFt > NosePastBarSlackFt;
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
