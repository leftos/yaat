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

    /// <summary>Published direction of an aerial refueling track being flown, or empty.</summary>
    public string Direction { get; init; } = string.Empty;

    public string? EntryPointId { get; init; }
    public string? ExitPointId { get; init; }
    public bool Marsa { get; init; }

    /// <summary>True when AP/1B authorises terrain following, which is flown lower in the block.</summary>
    public bool TerrainFollowing { get; init; }

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
            Direction = Direction,
            EntryPointId = EntryPointId,
            ExitPointId = ExitPointId,
            Marsa = Marsa,
            TerrainFollowing = TerrainFollowing,
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
            Direction = dto.Direction ?? string.Empty,
            EntryPointId = dto.EntryPointId,
            ExitPointId = dto.ExitPointId,
            Marsa = dto.Marsa,
            TerrainFollowing = dto.TerrainFollowing,
            PointNames = dto.PointNames ?? [],
            _started = dto.Started,
        };

    public override void OnStart(PhaseContext ctx)
    {
        var aircraft = ctx.Aircraft;
        var state = aircraft.MilitaryRoute;

        state.Designator = Designator;
        state.Kind = Kind;
        state.Direction = Direction;
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

        if (!IsAdditiveAirborneAdjustment(cmd) && cmd != CanonicalCommandType.ForceAltitude)
        {
            return CommandAcceptance.ClearsPhase;
        }

        // §9-2-6.h constrains *how* ATC may amend a MARSA route ("not in such a manner as to
        // compromise the MARSA provisions"); it does not forbid amending one, and §9-2-13.f NOTE 2
        // expressly contemplates post-rendezvous heading and altitude assignments. So the amendment
        // is accepted and MARSA is voided in OnCommandAccepted per §9-2-13.e, rather than the
        // keystroke being refused — a controller who watches separation responsibility land back on
        // them learns the rule; one whose input is rejected learns nothing.
        return CommandAcceptance.Allowed;
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

        if (IsAltitudeFamilyCommand(cmd) || cmd == CanonicalCommandType.ForceAltitude)
        {
            // The controller has superseded the published block. Stop re-arming until MTRA restores
            // it, rather than fighting the assignment every segment boundary.
            state.AltitudeSource = MilitaryRouteAltitudeSource.AssignedAltitude;
        }

        // §9-2-13.e: "Altitude or course changes issued will automatically void MARSA."
        if (state.Marsa)
        {
            state.Marsa = false;
            ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign}: MARSA voided on {Designator} — separation is ATC's again (7110.65 9-2-13.e)");
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
        // AtOrBelow re-arms too. §9-2-6.a offers "MAINTAIN AT OR BELOW (altitude)" as an
        // alternative *ceiling*, not as a release from the published profile — the route's floors
        // are the segment's minimum IFR altitudes and an at-or-below restriction cannot lower them.
        // Gating on RouteAltitudes alone made ApplyBlock's at-or-below branch unreachable and let
        // the aircraft descend below the segment floor unopposed.
        if (
            state.AltitudeSource
            is not (MilitaryRouteAltitudeSource.RouteAltitudes or MilitaryRouteAltitudeSource.AtOrBelow or MilitaryRouteAltitudeSource.AssignedBlock)
        )
        {
            return;
        }

        var route = NavigationDatabase.Instance.GetMilitaryRoute(Designator);
        if (route is null || ctx.Targets.NavigationRoute.Count == 0)
        {
            return;
        }

        // AP/1B chapter 5 publishes one block for the whole refueling entry rather than chapters
        // 2-4's per-segment blocks, so there is no segment to look up and nothing changes as the
        // aircraft sequences the track.
        if (route.IsAerialRefueling)
        {
            ApplyRefuelingBlock(ctx, route);
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

    /// <summary>
    /// Arm the refueling block: the one the controller assigned under §9-2-13 "MAINTAIN BLOCK
    /// (altitude) THROUGH (altitude)", or the track's published block when none was assigned.
    /// </summary>
    private void ApplyRefuelingBlock(PhaseContext ctx, MilitaryRoute route)
    {
        var state = ctx.Aircraft.MilitaryRoute;
        double? floor = state.AssignedFloorFt ?? route.RouteAltitude.FloorFt;
        double? ceiling = state.AssignedCeilingFt ?? route.RouteAltitude.CeilingFt;
        if (floor is null || ceiling is null)
        {
            return;
        }

        ctx.Targets.AltitudeFloor = floor;
        ctx.Targets.AltitudeCeiling = ceiling;
        // Mid-block rather than the training-route profile's just-above-the-floor. §9-2-13.f NOTE 3
        // has refueling normally occupying at least three consecutive altitudes, and §9-2-13.i.1-2
        // has the tanker departing the track from the top of the block and the receiver from the
        // bottom, so the middle is where the operation actually sits.
        ctx.Targets.TargetAltitude = floor.Value + ((ceiling.Value - floor.Value) / 2);
        ctx.Targets.AssignedAltitude = null;

        if (state.AppliedFloorFt != floor || state.AppliedCeilingFt != ceiling)
        {
            ctx.Logger.LogDebug("{Callsign}: {Route} refueling block {Floor} - {Ceiling} ft MSL", ctx.Aircraft.Callsign, Designator, floor, ceiling);
        }

        state.AppliedFloorFt = floor;
        state.AppliedCeilingFt = ceiling;
    }

    private void ApplyBlock(PhaseContext ctx, MilitaryRoutePoint point)
    {
        var block = point.Altitude;
        if (block.Kind is MilitaryRouteAltitudeKind.None or MilitaryRouteAltitudeKind.AsAssigned or MilitaryRouteAltitudeKind.Unparsed)
        {
            return;
        }

        double? reference = block.HasAglBound ? ResolveGroundReference(ctx, point) : null;
        if (block.HasAglBound && reference is null)
        {
            ctx.Logger.LogWarning(
                "{Callsign}: no terrain reference near {Route} point {Point}; leaving its AGL bound unenforced",
                ctx.Aircraft.Callsign,
                Designator,
                point.Id
            );
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
        // Command a target *inside* the block rather than leaving it null. With no target the
        // aircraft only moves when outside the block, so one entering IR-149 at 8,000 would settle
        // on the 6,000 ceiling and sit there for the whole route — 6,000 being inside every later
        // block too. That is the opposite of what a training route is for: AIM 3-5-2 describes low
        // level tactical training, and on a scope MTR traffic is recognisable precisely by Mode C
        // working through the block segment by segment. The bounds stay armed as hard limits.
        ctx.Targets.TargetAltitude = ProfileAltitude(floor, ceiling);
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

    /// <summary>
    /// The MSL bound for a published altitude, or null when an AGL bound cannot be resolved.
    ///
    /// An unresolvable AGL bound is deliberately left <em>unenforced</em> rather than defaulted to
    /// sea level: a "05 AGL" floor armed at 500 ft MSL over the Great Basin sits thousands of feet
    /// underground, and the aircraft would actively descend toward it. An unarmed floor is a
    /// visible non-behaviour; an armed subterranean floor is a wrong one.
    /// </summary>
    private static double? ResolveBound(int? value, AltitudeReference? reference, double? groundReferenceFt)
    {
        if (value is null)
        {
            return null;
        }

        if (reference != AltitudeReference.Agl)
        {
            return value.Value;
        }

        return groundReferenceFt is { } ground ? value.Value + ground : null;
    }

    /// <summary>
    /// Best available stand-in for terrain height at a route point, or null when nothing resolves.
    ///
    /// YAAT has no terrain model. The MVA database is the closest thing: a sector's minimum
    /// vectoring altitude is the highest terrain or obstacle in it plus 1,000 ft (2,000 in
    /// designated mountainous terrain), so MVA minus that buffer is a defensible upper bound on
    /// local ground — and erring high is the safe direction for a floor. Nearest-airport elevation
    /// errs the wrong way, because airports sit in valleys, so it is only the fallback.
    /// </summary>
    private static double? ResolveGroundReference(PhaseContext ctx, MilitaryRoutePoint point)
    {
        double? fromMva = Data.Mva.MvaDatabase.Default.GetFloorFtMsl(point.Position) is { } mva ? mva - MvaObstacleBufferFt : null;
        double? fromAirport = NavigationDatabase.Instance.FindNearestAirportElevation(point.Position, AglReferenceRangeNm);

        return (fromMva, fromAirport) switch
        {
            ({ } m, { } a) => Math.Max(m, a),
            ({ } m, null) => m,
            (null, { } a) => a,
            _ => null,
        };
    }

    /// <summary>Obstacle-clearance buffer folded into a published MVA (14 CFR 91.177 / FAA Order JO 7210.37).</summary>
    private const double MvaObstacleBufferFt = 1000;

    /// <summary>
    /// The altitude flown within a published block.
    ///
    /// A terrain-following route is flown low in the block; anything else sits a margin above the
    /// floor. AP/1B publishes no per-segment target — the crew flies the tactical profile — so this
    /// is YAAT's choice of a representative profile, not published data.
    /// </summary>
    private double? ProfileAltitude(double? floor, double? ceiling)
    {
        if (floor is not { } low)
        {
            return ceiling;
        }

        if (ceiling is not { } high || high <= low)
        {
            return low;
        }

        double target = TerrainFollowing ? low + (high - low) * TerrainFollowingBlockFraction : low + ProfileMarginFt;
        return Math.Clamp(target, low, high);
    }

    /// <summary>Margin above a published floor for a route with no terrain-following authorisation.</summary>
    private const double ProfileMarginFt = 500;

    /// <summary>Where in the block a terrain-following route is flown.</summary>
    private const double TerrainFollowingBlockFraction = 0.25;

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
