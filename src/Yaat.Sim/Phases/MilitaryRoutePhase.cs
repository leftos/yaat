using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases;

/// <summary>
/// Flies a published AP/1B military training route: lateral guidance along the route's points, and
/// the altitude block published for each segment.
///
/// A phase rather than a queued <see cref="CommandBlock"/> because the vertical constraint changes
/// at every route point with no new clearance, and it has to be re-asserted from durable state.
/// <c>CommandBlock.ApplyAction</c> is not restored after a snapshot — only <c>SourceCommandText</c>
/// survives — so anything built on queue closures evaporates on replay or rewind. A phase has the
/// full four-part DTO round-trip.
///
/// Re-assertion is not optional: <see cref="FlightCommandHandler"/>'s climb, descend and force
/// altitude paths all null both <see cref="ControlTargets.AltitudeFloor"/> and
/// <see cref="ControlTargets.AltitudeCeiling"/>. That is correct when a controller altitude
/// supersedes the block — and it is exactly why the block cannot simply live in
/// <see cref="ControlTargets"/> and be left there.
/// </summary>
public sealed class MilitaryRoutePhase : Phase
{
    /// <summary>
    /// How far the nearest airport may be when resolving an AGL bound to MSL.
    ///
    /// YAAT has no terrain model, so an AGL floor along a route crossing arbitrary terrain is
    /// resolved against the nearest airport's field elevation. The result is wrong by the
    /// terrain-to-airport delta — routinely thousands of feet in the mountain west — which is why
    /// the resolved MSL pair is surfaced to the instructor rather than kept implicit.
    /// </summary>
    private const double AglReferenceRangeNm = 100;

    private bool _started;

    public required string Designator { get; init; }
    public required MilitaryRouteType Kind { get; init; }
    public string? EntryPointId { get; init; }
    public string? ExitPointId { get; init; }
    public bool Marsa { get; init; }

    /// <summary>Ordered synthetic fix names of the cleared span, in the direction of flight.</summary>
    public required IReadOnlyList<string> PointNames { get; init; }

    public override string Name => $"MTR {Designator}";

    /// <summary>
    /// The normal speed schedule still runs. The 14 CFR 91.117(a) waiver is applied in
    /// <see cref="FlightPhysics"/> from <see cref="AircraftMilitaryRoute.SpeedLimitWaived"/>, not by
    /// this phase taking over speed.
    /// </summary>
    public override bool ManagesSpeed => false;

    public override PhaseDto ToSnapshot() =>
        new MilitaryRoutePhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            Designator = Designator,
            Kind = (int)Kind,
            EntryPointId = EntryPointId,
            ExitPointId = ExitPointId,
            Marsa = Marsa,
            PointNames = [.. PointNames],
            Started = _started,
        };

    public static MilitaryRoutePhase FromSnapshot(MilitaryRoutePhaseDto dto) =>
        new()
        {
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            Designator = dto.Designator,
            Kind = (MilitaryRouteType)dto.Kind,
            EntryPointId = dto.EntryPointId,
            ExitPointId = dto.ExitPointId,
            Marsa = dto.Marsa,
            PointNames = dto.PointNames ?? [],
            _started = dto.Started,
        };

    public override void OnStart(PhaseContext ctx)
    {
        var aircraft = ctx.Aircraft;
        var state = aircraft.MilitaryRoute;

        state.Designator = Designator;
        state.Kind = Kind;
        state.EntryPointId = EntryPointId;
        state.ExitPointId = ExitPointId;
        state.Marsa = Marsa;

        LoadRoute(ctx);

        // An aircraft arriving off a STAR carries the procedure's published speed as a SpeedCeiling
        // (AIM 5-4-1 NOTE 2). Left in place it would silently cap the whole training route.
        ctx.Targets.SpeedFloor = null;
        ctx.Targets.SpeedCeiling = null;

        ApplyEntrySquawk(ctx);
        _started = true;
        ReArmBlock(ctx);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_started)
        {
            OnStart(ctx);
        }

        var state = ctx.Aircraft.MilitaryRoute;

        if (ctx.Targets.NavigationRoute.Count == 0)
        {
            state.Status = MilitaryRouteStatus.Exited;
            return true;
        }

        if (state.Status == MilitaryRouteStatus.ClearedIn && IsEstablished(ctx))
        {
            state.Status = MilitaryRouteStatus.Established;
        }

        ReArmBlock(ctx);
        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        var state = ctx.Aircraft.MilitaryRoute;

        // docs/phases.md: any ControlTargets state a phase writes that physics depends on must be
        // reconciled when the phase clears. Leaving the block armed would pin the aircraft inside
        // published altitudes it is no longer cleared for.
        ctx.Targets.AltitudeFloor = null;
        ctx.Targets.AltitudeCeiling = null;
        state.AppliedFloorFt = null;
        state.AppliedCeilingFt = null;

        RestoreSquawk(ctx);

        if (state.Status is not MilitaryRouteStatus.Exited)
        {
            // The clearance is kept as a record rather than cleared: the instructor should still see
            // which route the aircraft was cleared into, and a later re-clearance can resume it.
            state.Status = endStatus == PhaseStatus.Completed ? MilitaryRouteStatus.Exited : MilitaryRouteStatus.VectoredOff;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        if (cmd == CanonicalCommandType.MaintainMilitaryRouteAltitudes)
        {
            return CommandAcceptance.Allowed;
        }

        if (!IsAdditiveAirborneAdjustment(cmd))
        {
            return CommandAcceptance.ClearsPhase;
        }

        // FAA JO 7110.65 §9-2-6.h: a clearance may amend or restrict operations on a route, except
        // where the route is designated MARSA — there ATC must not amend or restrict in a way that
        // would compromise the MARSA provisions.
        return Marsa
            ? CommandAcceptance.Rejected($"{Designator} is a MARSA route — ATC must not amend or restrict operations on it")
            : CommandAcceptance.Allowed;
    }

    public override void OnCommandAccepted(CanonicalCommandType cmd, PhaseContext ctx)
    {
        var state = ctx.Aircraft.MilitaryRoute;
        if (cmd == CanonicalCommandType.MaintainMilitaryRouteAltitudes)
        {
            state.AltitudeSource = MilitaryRouteAltitudeSource.RouteAltitudes;
            state.AssignedOverrideFt = null;
            return;
        }

        if (IsAltitudeFamilyCommand(cmd))
        {
            // The controller has superseded the published block. Stop re-arming until MTRA restores
            // it, rather than fighting the assignment every segment boundary.
            state.AltitudeSource = MilitaryRouteAltitudeSource.AssignedAltitude;
        }
    }

    private void LoadRoute(PhaseContext ctx)
    {
        var navDb = NavigationDatabase.Instance;
        var route = ctx.Targets.NavigationRoute;
        route.Clear();

        foreach (var name in PointNames)
        {
            var position = navDb.GetFixPosition(name);
            if (position is not null)
            {
                route.Add(new NavigationTarget { Name = name, Position = new LatLon(position.Value.Lat, position.Value.Lon) });
            }
        }
    }

    /// <summary>True once the aircraft has reached (and sequenced past) the cleared entry point.</summary>
    private bool IsEstablished(PhaseContext ctx)
    {
        var route = ctx.Targets.NavigationRoute;
        return PointNames.Count > 0 && route.Count > 0 && !string.Equals(route[0].Name, PointNames[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-resolve and re-apply the block for the segment currently being flown.
    ///
    /// The next navigation target names the point the current segment terminates at, and AP/1B
    /// publishes each altitude against that terminating point — confirmed against the FAA AIS
    /// MTRSegment layer, whose per-segment blocks match the row of the point the segment ends on.
    /// </summary>
    private void ReArmBlock(PhaseContext ctx)
    {
        var state = ctx.Aircraft.MilitaryRoute;
        if (state.AltitudeSource != MilitaryRouteAltitudeSource.RouteAltitudes)
        {
            return;
        }

        var route = NavigationDatabase.Instance.GetMilitaryRoute(Designator);
        if (route is null || ctx.Targets.NavigationRoute.Count == 0)
        {
            return;
        }

        var nextName = ctx.Targets.NavigationRoute[0].Name;
        int index = -1;
        for (int i = 0; i < route.Points.Count; i++)
        {
            if (string.Equals(route.Points[i].Name, nextName, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            // The controller has sent the aircraft somewhere that is not a point on this route.
            // Leave the last armed block alone rather than guessing at a segment.
            return;
        }

        state.CurrentSegmentIndex = index;
        ApplyBlock(ctx, route.Points[index]);
    }

    private void ApplyBlock(PhaseContext ctx, MilitaryRoutePoint point)
    {
        var block = point.Altitude;
        if (block.Kind is MilitaryRouteAltitudeKind.None or MilitaryRouteAltitudeKind.AsAssigned or MilitaryRouteAltitudeKind.Unparsed)
        {
            return;
        }

        double? reference = null;
        if (block.HasAglBound)
        {
            reference = NavigationDatabase.Instance.FindNearestAirportElevation(point.Position, AglReferenceRangeNm) ?? 0;
        }

        double? floor = ResolveBound(block.FloorFt, block.FloorReference, reference);
        double? ceiling = ResolveBound(block.CeilingFt, block.CeilingReference, reference);

        if (floor is null && ceiling is null)
        {
            return;
        }

        var state = ctx.Aircraft.MilitaryRoute;
        if (state.AltitudeSource == MilitaryRouteAltitudeSource.AtOrBelow && state.AssignedOverrideFt is { } restriction)
        {
            // §9-2-6.a "MAINTAIN AT OR BELOW (altitude)": the restriction caps the block, but the
            // route's published floor still applies.
            ceiling = ceiling is null ? restriction : Math.Min(ceiling.Value, restriction);
        }

        ctx.Targets.AltitudeFloor = floor;
        ctx.Targets.AltitudeCeiling = ceiling;
        // No single target: inside the block the aircraft holds altitude, and ResolveAltitudeGoal
        // corrects it toward whichever bound it is outside of.
        ctx.Targets.TargetAltitude = null;
        ctx.Targets.AssignedAltitude = null;

        if (state.AppliedFloorFt != floor || state.AppliedCeilingFt != ceiling)
        {
            ctx.Logger.LogDebug(
                "{Callsign}: {Route} segment to {Point} block {Floor} - {Ceiling} ft MSL",
                ctx.Aircraft.Callsign,
                Designator,
                point.Id,
                floor,
                ceiling
            );
        }

        state.AppliedFloorFt = floor;
        state.AppliedCeilingFt = ceiling;
    }

    private static double? ResolveBound(int? value, AltitudeReference? reference, double? groundReferenceFt) =>
        value is null ? null
        : reference == AltitudeReference.Agl ? value.Value + (groundReferenceFt ?? 0)
        : value.Value;

    /// <summary>
    /// AP/1B chapter 3 §V.F: squawk 4000 on a VR unless otherwise assigned. Chapter 2 §V.F leaves an
    /// IR on its ATC-assigned discrete code, which the aircraft already squawks, so there is nothing
    /// to do for IR or AR.
    /// </summary>
    private void ApplyEntrySquawk(PhaseContext ctx)
    {
        if (Kind is not (MilitaryRouteType.Vr or MilitaryRouteType.Sr))
        {
            return;
        }

        var transponder = ctx.Aircraft.Transponder;
        if (transponder.AssignedCode != 0)
        {
            return;
        }

        ctx.Aircraft.MilitaryRoute.PreRouteSquawk = transponder.Code;
        transponder.Code = MilitaryRouteSquawk;
    }

    private static void RestoreSquawk(PhaseContext ctx)
    {
        var state = ctx.Aircraft.MilitaryRoute;
        if (state.PreRouteSquawk is not { } previous)
        {
            return;
        }

        var transponder = ctx.Aircraft.Transponder;
        if (transponder.Code == MilitaryRouteSquawk)
        {
            transponder.Code = transponder.AssignedCode != 0 ? transponder.AssignedCode : previous;
        }

        state.PreRouteSquawk = null;
    }

    /// <summary>Beacon code 4000, the MTR-mission code listed in FAA JO 7110.65 §5-2-5.</summary>
    private const uint MilitaryRouteSquawk = 4000;
}
