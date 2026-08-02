using Yaat.Sim.Data;
using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Commands;

/// <summary>
/// Handlers for the AP/1B military training route clearances (FAA JO 7110.65 §9-2-6).
///
/// Separate from <see cref="NavigationCommandHandler"/>, which is already large and mixes JAWY,
/// JARR, JFAC, RFIS, RTIS and REPORT; military routes are a self-contained domain with their own
/// database dependency.
/// </summary>
public static class MilitaryRouteCommandHandler
{
    /// <summary>
    /// §9-2-6.a "CLEARED INTO IR (designator)". Installs <see cref="MilitaryRoutePhase"/> over the
    /// span from the cleared entry point to the cleared exit point.
    /// </summary>
    internal static CommandResult DispatchClearedInto(ClearedIntoMilitaryRouteCommand cmd, AircraftState aircraft)
    {
        var navDb = NavigationDatabase.Instance;
        var route = navDb.GetMilitaryRoute(cmd.Designator);
        if (route is null)
        {
            return new CommandResult(false, $"Unknown military route: {cmd.Designator}");
        }

        // §9-2-6 is titled IFR Military Training Routes; aerial refueling has its own clearance in
        // §9-2-13 with different phraseology and a block altitude clause, so the two verbs do not
        // cover for each other.
        if (route.IsAerialRefueling)
        {
            return new CommandResult(false, $"{route.Printed} is an aerial refueling track — use CAR");
        }

        // A route is one-way and course reversals are prohibited (AP/1B chapter 1 §V.B.1), so the
        // aircraft can only join at or ahead of its present position along the published order.
        int joinIndex = FindJoinIndex(aircraft, route.Points);
        if (joinIndex < 0)
        {
            return new CommandResult(false, $"Unable, {route.Printed} is one-way and the aircraft is past its exit point");
        }

        var pointNames = route.Points.Skip(joinIndex).Select(p => p.Name).ToList();
        var exitPointId = route.ExitPoints.Count > 0 ? route.ExitPoints[0] : route.Points[^1].Id;

        // Populated here rather than left to the phase's OnStart: a phase does not start until the
        // next tick, and the clearance has to be readable the moment the command is accepted — for
        // the readback, the strip, and any command issued in the same compound.
        var state = aircraft.MilitaryRoute;
        state.Clear();
        state.Designator = route.Designator;
        state.Kind = route.Type;
        state.EntryPointId = route.Points[joinIndex].Id;
        state.ExitPointId = exitPointId;
        state.Marsa = IsMarsa(route);
        state.AltitudeSource =
            cmd.AltitudeFt is null ? MilitaryRouteAltitudeSource.RouteAltitudes
            : cmd.AtOrBelow ? MilitaryRouteAltitudeSource.AtOrBelow
            : MilitaryRouteAltitudeSource.AssignedAltitude;
        state.AssignedOverrideFt = cmd.AltitudeFt;
        state.Status = MilitaryRouteStatus.ClearedIn;

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(
            new MilitaryRoutePhase
            {
                Designator = route.Designator,
                Kind = route.Type,
                EntryPointId = state.EntryPointId,
                ExitPointId = exitPointId,
                Marsa = state.Marsa,
                TerrainFollowing = route.TerrainFollowing,
                PointNames = pointNames,
            }
        );

        // The route takes over steering, exactly as an approach clearance does.
        aircraft.Targets.AssignedMagneticHeading = null;

        // FAA JO 7110.65 §9-2-6 is titled IFR Military Training Routes and every subparagraph is
        // about IRs: a VR is flown under VFR (AIM 3-5-2.c.2) and ATC does not clear an aircraft into
        // one, while an SR is not part of the MTR system at all (AP/1B chapter 4 §I). Putting traffic
        // on either is a legitimate thing to want from a trainer, so the command works — but the
        // "cleared into" phraseology is exactly what a controller student is being trained on, so it
        // is suppressed and the instructor is told why.
        if (route.Type is MilitaryRouteType.Vr or MilitaryRouteType.Sr)
        {
            aircraft.PendingWarnings.Add(
                $"{aircraft.Callsign}: {route.Printed} is a {(route.Type == MilitaryRouteType.Vr ? "VFR" : "slow")} route — "
                    + "ATC issues no clearance into one; the aircraft is placed on it as traffic"
            );
            return CommandDispatcher.Ok($"Proceeding on {route.Printed}");
        }

        if (cmd.AltitudeFt is { } altitude)
        {
            if (cmd.AtOrBelow)
            {
                aircraft.Targets.AltitudeCeiling = altitude;
                aircraft.Targets.TargetAltitude = aircraft.Altitude > altitude ? altitude : null;
                return CommandDispatcher.Ok($"Cleared into {route.Printed}, at or below {altitude:N0}");
            }

            aircraft.Targets.AssignedAltitude = altitude;
            aircraft.Targets.TargetAltitude = altitude;
            aircraft.Targets.AltitudeFloor = null;
            aircraft.Targets.AltitudeCeiling = null;
            return CommandDispatcher.Ok($"Cleared into {route.Printed}, maintain {altitude:N0}");
        }

        return CommandDispatcher.Ok($"Cleared into {route.Printed}, maintain {route.Designator} altitudes");
    }

    /// <summary>
    /// §9-2-13 "CLEARED TO CONDUCT REFUELING ALONG (number) TRACK" paired with "MAINTAIN BLOCK
    /// (altitude) THROUGH (altitude)".
    ///
    /// The direction is not part of the clearance: a track's two published directions are laterally
    /// offset parallels, so which one is meant follows from where the aircraft is and which way it is
    /// pointing.
    /// </summary>
    internal static CommandResult DispatchClearedToConductRefueling(ClearedToConductRefuelingCommand cmd, AircraftState aircraft)
    {
        var route = NavigationDatabase.Instance.GetMilitaryRoute(cmd.Designator);
        if (route is null)
        {
            return new CommandResult(false, $"Unknown aerial refueling track: {cmd.Designator}");
        }

        if (!route.IsAerialRefueling)
        {
            return new CommandResult(false, $"{route.Printed} is a military training route — use CMTR");
        }

        if (route.ArKind == MilitaryRouteArKind.Anchor)
        {
            return DispatchAnchor(cmd, aircraft, route);
        }

        var selection = SelectVariantForAircraft(aircraft, route);
        if (selection is not { } chosen)
        {
            return new CommandResult(false, $"Unable, the aircraft is past the exit of every published direction of {route.Printed}");
        }

        var (variant, joinIndex) = chosen;
        var pointNames = variant.Points.Skip(joinIndex).Select(p => p.Name).ToList();
        var exitPointId = variant.ExitPoints.Count > 0 ? variant.ExitPoints[0] : variant.Points[^1].Id;

        var state = aircraft.MilitaryRoute;
        state.Clear();
        state.Designator = route.Designator;
        state.Kind = route.Type;
        state.Direction = variant.Direction;
        state.EntryPointId = variant.Points[joinIndex].Id;
        state.ExitPointId = exitPointId;
        // §9-2-13 NOTE 3: MARSA begins when the tanker advises ATC it is accepting MARSA, so a track
        // is never MARSA merely for being one.
        state.Marsa = false;
        state.AltitudeSource = cmd.BlockFloorFt is null ? MilitaryRouteAltitudeSource.RouteAltitudes : MilitaryRouteAltitudeSource.AssignedBlock;
        state.AssignedFloorFt = cmd.BlockFloorFt;
        state.AssignedCeilingFt = cmd.BlockCeilingFt;
        state.Status = MilitaryRouteStatus.ClearedIn;

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(
            new MilitaryRoutePhase
            {
                Designator = route.Designator,
                Kind = route.Type,
                Direction = variant.Direction,
                EntryPointId = state.EntryPointId,
                ExitPointId = exitPointId,
                Marsa = false,
                TerrainFollowing = false,
                PointNames = pointNames,
            }
        );

        aircraft.Targets.AssignedMagneticHeading = null;

        var along = $"Cleared to conduct refueling along {route.Designator} track";
        if (cmd.BlockFloorFt is { } floor && cmd.BlockCeilingFt is { } ceiling)
        {
            return CommandDispatcher.Ok($"{along}, maintain block {floor:N0} through {ceiling:N0}");
        }

        var published = route.RouteAltitude;
        return published is { FloorFt: { } low, CeilingFt: { } high }
            ? CommandDispatcher.Ok($"{along}, maintain block {low:N0} through {high:N0}")
            : CommandDispatcher.Ok(along);
    }

    /// <summary>
    /// The anchor half of §9-2-13: in via the published entry points and ARIP, then orbit the
    /// published pattern until the aircraft is cleared out.
    ///
    /// Unlike a track, an anchor has no join index to compute — the published run-in is flown from
    /// wherever the aircraft is, because the entry points are the fixes ATC clears it to.
    /// </summary>
    private static CommandResult DispatchAnchor(ClearedToConductRefuelingCommand cmd, AircraftState aircraft, MilitaryRoute route)
    {
        var variant = SelectAnchorVariant(aircraft, route);
        if (variant is null)
        {
            return new CommandResult(false, $"Unable, {route.Printed} publishes no usable anchor geometry");
        }

        var entryNames = variant.Points.Select(p => p.Name).ToList();
        var patternNames = variant.Pattern.Select(p => p.Name).ToList();
        if (patternNames.Count == 0)
        {
            return new CommandResult(false, $"Unable, {route.Printed} publishes no orbit pattern");
        }

        var anchorPoint = variant.Points.FirstOrDefault(p => p.Role == MilitaryRoutePointRole.AnchorPoint);

        var state = aircraft.MilitaryRoute;
        state.Clear();
        state.Designator = route.Designator;
        state.Kind = route.Type;
        state.Direction = variant.Direction;
        state.EntryPointId = variant.Points.Count > 0 ? variant.Points[0].Id : anchorPoint?.Id;
        state.ExitPointId = variant.ExitPoints.Count > 0 ? variant.ExitPoints[0] : null;
        state.Marsa = false;
        state.AltitudeSource = cmd.BlockFloorFt is null ? MilitaryRouteAltitudeSource.RouteAltitudes : MilitaryRouteAltitudeSource.AssignedBlock;
        state.AssignedFloorFt = cmd.BlockFloorFt;
        state.AssignedCeilingFt = cmd.BlockCeilingFt;
        state.Status = MilitaryRouteStatus.ClearedIn;

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(
            new AerialRefuelingAnchorPhase
            {
                Designator = route.Designator,
                Direction = variant.Direction,
                EntryNames = entryNames,
                PatternNames = patternNames,
            }
        );

        aircraft.Targets.AssignedMagneticHeading = null;

        var along = $"Cleared to conduct refueling in the {route.Designator} anchor";
        double? floor = cmd.BlockFloorFt ?? route.RouteAltitude.FloorFt;
        double? ceiling = cmd.BlockCeilingFt ?? route.RouteAltitude.CeilingFt;
        return floor is { } low && ceiling is { } high
            ? CommandDispatcher.Ok($"{along}, maintain block {low:N0} through {high:N0}")
            : CommandDispatcher.Ok(along);
    }

    /// <summary>The published anchor direction whose run-in starts nearest the aircraft.</summary>
    private static MilitaryRouteVariant? SelectAnchorVariant(AircraftState aircraft, MilitaryRoute route)
    {
        MilitaryRouteVariant? best = null;
        double bestDistance = double.MaxValue;
        foreach (var variant in route.Variants)
        {
            if (variant.Points.Count == 0)
            {
                continue;
            }

            var entry = variant.Points[0].Position;
            double distance = GeoMath.DistanceNm(aircraft.Position.Lat, aircraft.Position.Lon, entry.Lat, entry.Lon);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = variant;
            }
        }

        return best;
    }

    /// <summary>§9-2-6.a "MAINTAIN IR (designator) ALTITUDE(S)".</summary>
    internal static CommandResult DispatchMaintainRouteAltitudes(AircraftState aircraft)
    {
        var state = aircraft.MilitaryRoute;
        if (!state.IsActive || state.Designator is null)
        {
            return new CommandResult(false, "Not established on a military training route");
        }

        state.AltitudeSource = MilitaryRouteAltitudeSource.RouteAltitudes;
        state.AssignedOverrideFt = null;
        aircraft.Targets.AssignedAltitude = null;
        aircraft.Targets.TargetAltitude = null;
        return CommandDispatcher.Ok($"Maintain {state.Designator} altitudes");
    }

    /// <summary>
    /// §9-2-6.b "CLEARED TO (destination) FROM IR (designator/exit fix) VIA (route)". Ends the route
    /// clearance and, when a route of flight is given, loads it.
    /// </summary>
    internal static CommandResult DispatchClearedOutOf(ClearedOutOfMilitaryRouteCommand cmd, AircraftState aircraft)
    {
        var state = aircraft.MilitaryRoute;
        if (state.Designator is null)
        {
            return new CommandResult(false, "Not on a military training route");
        }

        var designator = state.Designator;
        var exitPoint = state.ExitPointId;
        state.Status = MilitaryRouteStatus.Exited;

        // Clear() rather than nulling Phases: AircraftState.Phases is a plain property, so dropping
        // it skips MilitaryRoutePhase.OnEnd entirely — the VR beacon code would never be restored
        // and the armed block would stay on the strip after the aircraft had left the route.
        if (aircraft.Phases is not null)
        {
            aircraft.Phases.Clear(CommandDispatcher.BuildMinimalContext(aircraft));
            aircraft.Phases = null;
        }

        aircraft.Targets.AltitudeFloor = null;
        aircraft.Targets.AltitudeCeiling = null;

        if (cmd.Route is not null)
        {
            LoadRouteOfFlight(aircraft, cmd.Route);
        }

        var via = cmd.Route is null ? "" : $" via {cmd.Route}";
        var from = exitPoint is null ? designator : $"{designator} {exitPoint}";
        return CommandDispatcher.Ok($"Cleared to {cmd.Destination} from {from}{via}");
    }

    private static void LoadRouteOfFlight(AircraftState aircraft, string route)
    {
        var navDb = NavigationDatabase.Instance;
        aircraft.Targets.NavigationRoute.Clear();
        foreach (var name in navDb.ExpandRouteForNavigation(route, aircraft.FlightPlan.Departure))
        {
            var position = navDb.ResolveFixOrFrd(name);
            if (position is not null)
            {
                aircraft.Targets.NavigationRoute.Add(
                    new NavigationTarget { Name = name, Position = new LatLon(position.Value.Lat, position.Value.Lon) }
                );
            }
        }
    }

    /// <summary>
    /// The first published point at or ahead of the aircraft, or -1 when it is already past the end.
    /// Ahead is judged by the leg's own direction, so a route running back past the aircraft laterally
    /// does not read as joinable.
    /// </summary>
    private static int FindJoinIndex(AircraftState aircraft, IReadOnlyList<MilitaryRoutePoint> points)
    {
        int nearest = -1;
        double best = double.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i].Position;
            double distance = GeoMath.DistanceNm(aircraft.Position.Lat, aircraft.Position.Lon, point.Lat, point.Lon);
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        if (nearest < 0)
        {
            return -1;
        }

        // Joining at the nearest point would fly backwards down the leg the aircraft has already
        // passed, so join the next one when the nearest is behind. The final point has nothing
        // ahead of it — there is no forward span left to clear the aircraft into.
        if (IsBehind(aircraft, points[nearest].Position))
        {
            return nearest + 1 < points.Count ? nearest + 1 : -1;
        }

        return nearest;
    }

    private static bool IsBehind(AircraftState aircraft, LatLon point)
    {
        double bearing = GeoMath.BearingTo(aircraft.Position, point);
        double delta = Math.Abs(((bearing - aircraft.TrueHeading.Degrees + 540) % 360) - 180);
        return delta > 90;
    }

    /// <summary>
    /// The published direction the aircraft is positioned to fly, with the index it would join at.
    ///
    /// A refueling track's two directions are laterally offset parallels, so which one is meant is a
    /// question about where the aircraft is and which way it is pointing — not something the
    /// clearance says. Directions the aircraft is already past score out; among the rest the nearest
    /// join point wins.
    /// </summary>
    private static (MilitaryRouteVariant Variant, int JoinIndex)? SelectVariantForAircraft(AircraftState aircraft, MilitaryRoute route)
    {
        (MilitaryRouteVariant Variant, int JoinIndex)? best = null;
        double bestDistance = double.MaxValue;

        foreach (var variant in route.Variants)
        {
            int joinIndex = FindJoinIndex(aircraft, variant.Points);
            if (joinIndex < 0)
            {
                continue;
            }

            var join = variant.Points[joinIndex].Position;
            double distance = GeoMath.DistanceNm(aircraft.Position.Lat, aircraft.Position.Lon, join.Lat, join.Lon);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = (variant, joinIndex);
            }
        }

        return best;
    }

    /// <summary>
    /// MARSA comes from the route's published procedures, never from a typed command: §9-2-6.c
    /// establishes it by letter of agreement between the scheduling unit and the ATC facility.
    /// <para>
    /// Aerial refueling tracks are deliberately *not* MARSA merely for being AR tracks. §9-2-13
    /// NOTE 3 makes MARSA begin only once tanker and receiver have entered the refueling airspace
    /// and the tanker advises ATC it is accepting MARSA — a declaration on frequency, not a
    /// property of the track.
    /// </para>
    /// </summary>
    private static bool IsMarsa(MilitaryRoute route) => route.OriginatingActivity.Contains("MARSA", StringComparison.OrdinalIgnoreCase);
}
