using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Phases;

/// <summary>
/// Flies an AP/1B chapter 5 refueling <em>anchor</em>: in via the published entry points and ARIP to
/// the anchor point, then round the published orbit pattern until the clearance ends.
///
/// Separate from <see cref="MilitaryRoutePhase"/> because an anchor does not terminate. A track runs
/// from its ARIP to an exit and the phase completes when the points run out; an anchor is a holding
/// racetrack that the aircraft stays in until ATC clears it out, so an empty navigation route means
/// "fly another lap", not "done". Folding both into one phase would put that inversion behind a flag
/// in the middle of the tick.
///
/// The orbit is the pattern AP/1B prints — its corners, in the order published — rather than a
/// racetrack computed from a fix and an inbound course. The publication is the authority on the shape
/// and it already accounts for the anchor's ATC Assigned Airspace.
/// </summary>
public sealed class AerialRefuelingAnchorPhase : Phase
{
    private bool _started;
    private int _laps;

    public required string Designator { get; init; }

    /// <summary>Published direction of the anchor being flown, or empty when it publishes one.</summary>
    public string Direction { get; init; } = string.Empty;

    /// <summary>Synthetic fix names flown once, in order, to reach the anchor point.</summary>
    public required IReadOnlyList<string> EntryNames { get; init; }

    /// <summary>Synthetic fix names of the orbit corners, flown on repeat.</summary>
    public required IReadOnlyList<string> PatternNames { get; init; }

    public override string Name => $"AR anchor {Designator}";

    /// <summary>
    /// Speed is left to the normal schedule and to any controller assignment, the same as on a
    /// refueling track. The 91.117(a) waiver is applied in <see cref="FlightPhysics"/> from
    /// <see cref="AircraftMilitaryRoute.SpeedLimitWaived"/>.
    /// </summary>
    public override bool ManagesSpeed => false;

    /// <summary>Laps completed round the orbit, for the instructor and for tests.</summary>
    public int Laps => _laps;

    public override PhaseDto ToSnapshot() =>
        new AerialRefuelingAnchorPhaseDto
        {
            Status = (int)Status,
            ElapsedSeconds = ElapsedSeconds,
            Requirements = SnapshotRequirements(),
            Designator = Designator,
            Direction = Direction,
            EntryNames = [.. EntryNames],
            PatternNames = [.. PatternNames],
            Started = _started,
            Laps = _laps,
        };

    public static AerialRefuelingAnchorPhase FromSnapshot(AerialRefuelingAnchorPhaseDto dto) =>
        new()
        {
            Status = (PhaseStatus)dto.Status,
            ElapsedSeconds = dto.ElapsedSeconds,
            Designator = dto.Designator,
            Direction = dto.Direction ?? string.Empty,
            EntryNames = dto.EntryNames ?? [],
            PatternNames = dto.PatternNames ?? [],
            _started = dto.Started,
            _laps = dto.Laps,
        };

    public override void OnStart(PhaseContext ctx)
    {
        var state = ctx.Aircraft.MilitaryRoute;
        state.Designator = Designator;
        state.Kind = MilitaryRouteType.Ar;
        state.Direction = Direction;

        LoadNames(ctx, EntryNames.Count > 0 ? EntryNames : PatternNames);

        // An aircraft arriving off a STAR carries the procedure's published speed as a SpeedCeiling
        // (AIM 5-4-1 NOTE 2). Left in place it would silently cap the whole refueling operation.
        ctx.Targets.SpeedFloor = null;
        ctx.Targets.SpeedCeiling = null;

        _started = true;
        ArmBlock(ctx);
    }

    public override bool OnTick(PhaseContext ctx)
    {
        if (!_started)
        {
            OnStart(ctx);
        }

        var state = ctx.Aircraft.MilitaryRoute;

        // An empty route means the aircraft has flown everything queued: the run-in on the first
        // pass, an orbit lap afterwards. Either way the next thing to fly is another lap -- an
        // anchor is left only by clearance, never by running out of points.
        if (ctx.Targets.NavigationRoute.Count == 0)
        {
            if (PatternNames.Count == 0)
            {
                // Nothing to orbit (AR662V publishes no pattern). Hand control back rather than
                // spinning on an empty route.
                state.Status = MilitaryRouteStatus.Exited;
                return true;
            }

            state.Status = MilitaryRouteStatus.Established;
            _laps++;
            LoadNames(ctx, PatternNames);
            ctx.Logger.LogDebug("{Callsign}: {Route} anchor lap {Lap}", ctx.Aircraft.Callsign, Designator, _laps);
        }
        else if (state.Status == MilitaryRouteStatus.ClearedIn && IsAtAnchor(ctx))
        {
            state.Status = MilitaryRouteStatus.Established;
        }

        ArmBlock(ctx);
        return false;
    }

    public override void OnEnd(PhaseContext ctx, PhaseStatus endStatus)
    {
        var state = ctx.Aircraft.MilitaryRoute;

        // docs/phases.md: ControlTargets state a phase writes that physics depends on has to be
        // reconciled when the phase clears, or the aircraft stays pinned inside a block it is no
        // longer cleared for.
        ctx.Targets.AltitudeFloor = null;
        ctx.Targets.AltitudeCeiling = null;
        state.AppliedFloorFt = null;
        state.AppliedCeilingFt = null;

        if (state.Status is not MilitaryRouteStatus.Exited)
        {
            state.Status = endStatus == PhaseStatus.Completed ? MilitaryRouteStatus.Exited : MilitaryRouteStatus.VectoredOff;
        }
    }

    public override CommandAcceptance CanAcceptCommand(CanonicalCommandType cmd)
    {
        // Same shape as a refueling track: an amendment is accepted rather than refused, and
        // §9-2-13.e voids MARSA when one is issued.
        if (!IsAdditiveAirborneAdjustment(cmd) && cmd != CanonicalCommandType.ForceAltitude)
        {
            return CommandAcceptance.ClearsPhase;
        }

        return CommandAcceptance.Allowed;
    }

    public override void OnCommandAccepted(CanonicalCommandType cmd, PhaseContext ctx)
    {
        var state = ctx.Aircraft.MilitaryRoute;
        if (IsAltitudeFamilyCommand(cmd) || cmd == CanonicalCommandType.ForceAltitude)
        {
            state.AltitudeSource = MilitaryRouteAltitudeSource.AssignedAltitude;
        }

        // §9-2-13.e: "Altitude or course changes issued will automatically void MARSA."
        if (state.Marsa)
        {
            state.Marsa = false;
            ctx.Aircraft.PendingWarnings.Add($"{ctx.Aircraft.Callsign}: MARSA voided on {Designator} — separation is ATC's again (7110.65 9-2-13.e)");
        }
    }

    private static void LoadNames(PhaseContext ctx, IReadOnlyList<string> names)
    {
        var navDb = NavigationDatabase.Instance;
        var route = ctx.Targets.NavigationRoute;
        route.Clear();

        foreach (var name in names)
        {
            var position = navDb.GetFixPosition(name);
            if (position is not null)
            {
                route.Add(new NavigationTarget { Name = name, Position = new LatLon(position.Value.Lat, position.Value.Lon) });
            }
        }
    }

    /// <summary>True once the run-in has been flown and the aircraft is working the orbit itself.</summary>
    private bool IsAtAnchor(PhaseContext ctx)
    {
        var route = ctx.Targets.NavigationRoute;
        return route.Count > 0 && PatternNames.Contains(route[0].Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Arm the refueling block: the controller's assigned block under §9-2-13 "MAINTAIN BLOCK
    /// (altitude) THROUGH (altitude)", or the anchor's published block when none was assigned.
    /// </summary>
    private void ArmBlock(PhaseContext ctx)
    {
        var state = ctx.Aircraft.MilitaryRoute;
        if (state.AltitudeSource is MilitaryRouteAltitudeSource.AssignedAltitude)
        {
            return;
        }

        var route = NavigationDatabase.Instance.GetMilitaryRoute(Designator);
        double? floor = state.AssignedFloorFt ?? route?.RouteAltitude.FloorFt;
        double? ceiling = state.AssignedCeilingFt ?? route?.RouteAltitude.CeilingFt;
        if (floor is null || ceiling is null)
        {
            return;
        }

        ctx.Targets.AltitudeFloor = floor;
        ctx.Targets.AltitudeCeiling = ceiling;
        ctx.Targets.TargetAltitude = ProfileAltitude(floor, ceiling);
        ctx.Targets.AssignedAltitude = null;
        state.AppliedFloorFt = floor;
        state.AppliedCeilingFt = ceiling;
    }

    /// <summary>
    /// Where in the block to fly. §9-2-13.f NOTE 3 has refueling normally occupying at least three
    /// consecutive altitudes, and §9-2-13.i.1-2 has the tanker leaving from the top of the block and
    /// the receiver from the bottom, so the middle is the neutral place to sit.
    /// </summary>
    private static double ProfileAltitude(double? floor, double? ceiling) =>
        (floor, ceiling) switch
        {
            ({ } low, { } high) => low + ((high - low) / 2),
            ({ } low, null) => low,
            (null, { } high) => high,
            _ => 0,
        };
}
